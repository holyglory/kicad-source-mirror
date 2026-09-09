using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KiCad.Automation.Validation;

public sealed record EvidenceFile(string Path, long Bytes, string Sha256);
public sealed record ValidationStep(string Name, int ExitCode, DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt, string StandardOutput, string StandardError);
public sealed record ValidationReceipt(int SchemaVersion, string Platform, string ProcessArchitecture,
    string OperatingSystemArchitecture, string ExpectedCommit, string? VerifiedCommit,
    string? BuilderCommit, string? BuilderStatus, string ToolchainSha256, string NativeTestSelection,
    string DotnetTestSelection, string Status, string? Failure, IReadOnlyList<ValidationStep> Steps,
    IReadOnlyList<EvidenceFile> Files, string? TargetArchitecture = null,
    IReadOnlyList<NativeBinaryEvidence>? NativeBinaries = null, MacManagedRuntimeEvidence? ManagedRuntime = null);
public sealed record ValidationResult(int SchemaVersion, string Status, string ExpectedCommit,
    string ReceiptSha256, string ArchiveSha256, string Archive, bool CrossPlatformReady = false);

public static partial class Evidence
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitPattern();

    public static void RequireCommit(string commit)
    {
        if (!CommitPattern().IsMatch(commit))
            throw new ArgumentException("A full, lowercase 40-character Git commit SHA is required; branches and abbreviated SHAs are not accepted.");
    }

    public static string Hash(string path)
    {
        using FileStream input = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }

    public static async Task<ValidationResult> SealAsync(string directory, string evidenceDirectory,
        ValidationReceipt receipt)
    {
        string receiptPath = Path.Combine(evidenceDirectory, "receipt.json");
        await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(receipt, JsonOptions));
        string archivePath = Path.Combine(directory, "evidence.tar.gz");
        await using (var file = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write))
        await using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
            TarFile.CreateFromDirectory(evidenceDirectory, gzip, includeBaseDirectory: false);
        var result = new ValidationResult(1, receipt.Status, receipt.ExpectedCommit,
            Hash(receiptPath), Hash(archivePath), archivePath);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(result, JsonOptions));
        return result;
    }

    // Integrity checking is deliberately separate from Mac execution. A checksum
    // is not an attestation and cannot manufacture missing rendered acceptance.
    public static async Task VerifyAsync(string resultPath, string archivePath, string commit,
        CancellationToken cancellationToken = default, string? architecture = null)
    {
        RequireCommit(commit);
        if (architecture is not null) _ = MacArchitecture.CmakeName(architecture);
        ValidationResult result = JsonSerializer.Deserialize<ValidationResult>(await File.ReadAllTextAsync(resultPath, cancellationToken))
            ?? throw new InvalidDataException("Invalid result document.");
        if (result.SchemaVersion != 1 || result.ExpectedCommit != commit || result.CrossPlatformReady
            || result.Status != "checks_passed" || Hash(archivePath) != result.ArchiveSha256)
            throw new InvalidDataException("Receipt identity, status or archive checksum does not match.");
        using var input = File.OpenRead(archivePath);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        ValidationReceipt? receipt = null;
        var files = new Dictionary<string, (long Size, string Hash)>(StringComparer.Ordinal);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.EntryType == TarEntryType.Directory) continue;
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                || (entry.DataStream is null && entry.Length != 0))
                throw new InvalidDataException("Evidence archives must contain ordinary files, not links or special entries.");
            Stream data = entry.DataStream ?? Stream.Null;
            string name = entry.Name.StartsWith("./", StringComparison.Ordinal) ? entry.Name[2..] : entry.Name;
            if (Path.IsPathRooted(name) || name.Split('/').Any(part => part == "..") || files.ContainsKey(name))
                throw new InvalidDataException("Invalid or repeated evidence path.");
            if (name == "receipt.json")
            {
                if (entry.Length > 4 * 1024 * 1024 || receipt is not null) throw new InvalidDataException("Invalid receipt size or duplicate receipt.");
                using var bytes = new MemoryStream();
                await data.CopyToAsync(bytes, cancellationToken);
                if (Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray())) != result.ReceiptSha256)
                    throw new InvalidDataException("Receipt checksum does not match.");
                receipt = JsonSerializer.Deserialize<ValidationReceipt>(bytes.ToArray());
            }
            else
                files.Add(name, (entry.Length, Convert.ToHexStringLower(await SHA256.HashDataAsync(data, cancellationToken))));
        }
        if (receipt is null || receipt.SchemaVersion is not (1 or 2 or 3) || receipt.Platform != "macos"
            || receipt.VerifiedCommit != commit || receipt.ExpectedCommit != commit || receipt.Status != "checks_passed"
            || receipt.Steps.Count == 0 || receipt.Steps.Any(step => step.ExitCode != 0))
            throw new InvalidDataException("The receipt does not describe successful selected Mac checks for this exact commit.");
        if (architecture is not null && (receipt.SchemaVersion < 2 || receipt.TargetArchitecture != architecture))
            throw new InvalidDataException("Receipt does not identify the requested Mac target.");
        if (receipt.SchemaVersion >= 2)
        {
            if (receipt.TargetArchitecture is null || receipt.NativeBinaries is null
                || receipt.NativeBinaries.Count != 2 || receipt.NativeBinaries.Any(b => b is null)
                || !receipt.NativeBinaries.Select(b => b.Name).Order().SequenceEqual(new[] { "kicad", "kicad-cli" }))
                throw new InvalidDataException("Architecture-aware receipts need both native executable identities.");
            if (!Enum.TryParse<System.Runtime.InteropServices.Architecture>(receipt.ProcessArchitecture, out var processArchitecture))
                throw new InvalidDataException("Receipt process architecture is invalid.");
            MacArchitecture.RequireExecutionTarget(receipt.TargetArchitecture, processArchitecture);
            foreach (var binary in receipt.NativeBinaries)
            {
                MacArchitecture.RequireBinaryTarget(receipt.TargetArchitecture, binary.Architectures);
                if (binary.Sha256 is null || binary.Sha256.Length != 64 || binary.Sha256.Any(c => !char.IsAsciiHexDigitLower(c)))
                    throw new InvalidDataException("Native executable checksum is invalid.");
            }
        }
        if (receipt.SchemaVersion == 3)
        {
            if (receipt.ManagedRuntime is null) throw new InvalidDataException("The Mac runtime receipt is missing.");
            MacManagedRuntime.ValidateEvidence(receipt.ManagedRuntime, receipt.TargetArchitecture!);
        }
        if (receipt.Files.Count != files.Count || receipt.Files.Select(file => file.Path).Distinct().Count() != receipt.Files.Count)
            throw new InvalidDataException("Evidence inventory does not match the archive.");
        foreach (EvidenceFile file in receipt.Files)
            if (!files.TryGetValue(file.Path, out var actual) || actual.Size != file.Bytes || actual.Hash != file.Sha256)
                throw new InvalidDataException($"Evidence checksum does not match: {file.Path}");
    }
}
