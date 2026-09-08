using System.Security.Cryptography;
using System.Text.Json;
using KiCad.Automation.Distribution;

namespace KiCad.Automation.Validation;

public sealed record PublisherKeyReceipt(string PublicKeyPath, string PublicKeySha256);
public sealed record SignedReleaseReceipt(string EnvelopePath, string EnvelopeSha256, string PayloadSha256,
    string PublisherKeySha256, long Sequence, string Commit, string Channel);

public static class UpdatePublishing
{
    public static async Task<PublisherKeyReceipt> CreatePublisherAsync(string privatePath, string publicPath, CancellationToken token)
    {
        RequireNewPath(privatePath);
        RequireNewPath(publicPath);
        if (privatePath == publicPath) throw new ArgumentException("Use different private and public key files.");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] privateBytes = key.ExportPkcs8PrivateKey();
        try { await WriteNewAsync(privatePath, privateBytes, true, token); }
        finally { CryptographicOperations.ZeroMemory(privateBytes); }
        // A failed public export retains the new private key. ExportPublisher
        // can finish provisioning without replacing that identity.
        return await ExportPublisherAsync(privatePath, publicPath, token);
    }

    public static async Task<PublisherKeyReceipt> ExportPublisherAsync(string privatePath, string publicPath, CancellationToken token)
    {
        RequireNewPath(publicPath);
        using var key = await ReadPrivateAsync(privatePath, token);
        byte[] bytes = key.ExportSubjectPublicKeyInfo();
        await WriteNewAsync(publicPath, bytes, false, token);
        return new(publicPath, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    public static async Task<SignedReleaseReceipt> SignAsync(UpdateRelease release, string artifactRoot, string privatePath,
        string output, string? previousEnvelope, CancellationToken token)
    {
        RequireNewPath(output);
        if (!Path.IsPathFullyQualified(artifactRoot) || !Directory.Exists(artifactRoot))
            throw new ArgumentException("Use an existing absolute artifact directory.");
        using var key = await ReadPrivateAsync(privatePath, token);
        byte[] envelope = UpdateManifestCodec.Sign(release, key); // validates typed identities before path use
        byte[] publicKey = key.ExportSubjectPublicKeyInfo();
        if (previousEnvelope is not null)
        {
            var previous = UpdateManifestCodec.Verify(await ReadBoundedAsync(previousEnvelope,
                UpdateManifestCodec.MaximumEnvelopeBytes, token), publicKey, release.Channel);
            if (release.Sequence <= previous.Release.Sequence)
                throw new InvalidDataException("A new signed release must advance the previous sequence.");
        }
        foreach (var artifact in release.Artifacts)
        {
            string path = Path.Combine(artifactRoot, artifact.FileName);
            var info = new FileInfo(path);
            if (!info.Exists || info.LinkTarget is not null || info.Length != artifact.Bytes)
                throw new InvalidDataException("A release artifact is absent, linked or has the wrong size.");
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token)) != artifact.Sha256)
                throw new InvalidDataException("A release artifact failed its declared checksum.");
        }
        var verified = UpdateManifestCodec.Verify(envelope, publicKey, release.Channel);
        await WriteNewAsync(output, envelope, false, token);
        return new(output, Convert.ToHexStringLower(SHA256.HashData(envelope)), verified.PayloadSha256,
            verified.PublisherKeySha256, release.Sequence, release.Commit, release.Channel);
    }

    public static async Task<UpdateRelease> ReadReleaseAsync(string path, CancellationToken token)
    {
        byte[] bytes = await ReadBoundedAsync(path, UpdateManifestCodec.MaximumPayloadBytes, token);
        using var document = JsonDocument.Parse(bytes);
        RejectDuplicates(document.RootElement);
        return JsonSerializer.Deserialize<UpdateRelease>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("Release declaration is missing.");
    }

    private static async Task<ECDsa> ReadPrivateAsync(string path, CancellationToken token)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Publisher keys require Unix owner-only file permissions.");
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Publisher keys require an operator-owned Unix key file.");
        if ((File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new InvalidDataException("The publisher private key must be accessible only to its owner.");
        byte[] bytes = await ReadBoundedAsync(path, 4096, token);
        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(bytes, out int consumed);
            if (consumed != bytes.Length || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
                throw new InvalidDataException("Use one complete P-256 private key.");
            return key;
        }
        catch { key.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, int maximum, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Use absolute publication paths.");
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || info.Length < 1 || info.Length > maximum)
            throw new InvalidDataException("Publication input must be a bounded regular non-linked file.");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > maximum) throw new InvalidDataException("Publication input changed during reading.");
        byte[] bytes = new byte[(int)file.Length];
        await file.ReadExactlyAsync(bytes, token);
        return bytes;
    }

    private static void RequireNewPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(Path.GetDirectoryName(path)))
            throw new ArgumentException("Use an absolute output path with an existing parent directory.");
        if (File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null)
            throw new IOException("Publication output already exists; it will not be replaced.");
    }

    private static async Task WriteNewAsync(string path, byte[] bytes, bool privateFile, CancellationToken token)
    {
        RequireNewPath(path);
        token.ThrowIfCancellationRequested();
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            options.UnixCreateMode = privateFile ? UnixFileMode.UserRead | UnixFileMode.UserWrite
                : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        else if (privateFile) throw new PlatformNotSupportedException("Private key creation requires Unix owner-only permissions.");
        string pending = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        bool owned = false;
        try
        {
            await using (var file = new FileStream(pending, options))
            {
                owned = true;
                await file.WriteAsync(bytes, token);
                await file.FlushAsync(token);
                file.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(pending, path, overwrite: false);
            owned = false;
        }
        finally { if (owned) File.Delete(pending); }
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException("Repeated release declaration property."); RejectDuplicates(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }
}
