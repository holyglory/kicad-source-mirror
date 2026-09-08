using System.Text.Json;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace KiCad.Automation.Distribution;

public sealed record UpdateActivation(string OperationId, string PreviousTarget, string Target, string VersionTarget, string Status);

/// <summary>Switches a dedicated installation's relative current link only.
/// Does not stop/restart processes or delete old versions. The native caller
/// must obtain the user's Update action and handle its editor lifecycle.</summary>
public static class LinuxUpdateActivation
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task InitializeAsync(string installationRoot, string firstVersion,
        CancellationToken token = default)
    {
        RequireLinux();
        RequireRoot(installationRoot);
        if (Directory.Exists(installationRoot) || File.Exists(installationRoot))
            throw new IOException("The dedicated installation root must not already exist.");
        if (!Path.IsPathFullyQualified(firstVersion) || !Directory.Exists(firstVersion))
            throw new ArgumentException("Initial version must be an existing absolute version directory.", nameof(firstVersion));
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(installationRoot);
        Directory.CreateDirectory(Path.Combine(installationRoot, "operations"));
        string selections = Directory.CreateDirectory(Path.Combine(installationRoot, "selections")).FullName;
        await WriteNewAsync(Path.Combine(installationRoot, "installation.json"), new
        { schemaVersion = 1, product = "kicad-codex", installationId = Guid.NewGuid().ToString("D") }, token);
        string firstSelection = "initial-" + Guid.NewGuid().ToString("D");
        Directory.CreateSymbolicLink(Path.Combine(selections, firstSelection), Path.GetRelativePath(selections, firstVersion));
        Directory.CreateSymbolicLink(Path.Combine(installationRoot, "current"), "selections/" + firstSelection);
    }

    public static Task<UpdateActivation> SwitchAsync(string installationRoot, string expectedTarget,
        string nextVersion, Guid operationId, CancellationToken token = default) =>
        SwitchAsync(installationRoot, expectedTarget, nextVersion, operationId, null, token);

    internal static async Task<UpdateActivation> SwitchAsync(string installationRoot, string expectedTarget,
        string nextVersion, Guid operationId, Action? beforeSwitch, CancellationToken token)
    {
        RequireLinux();
        RequireRoot(installationRoot);
        if (operationId == Guid.Empty) throw new ArgumentException("Use a non-empty update operation ID.", nameof(operationId));
        if (!Path.IsPathFullyQualified(nextVersion) || !Directory.Exists(nextVersion))
            throw new ArgumentException("Candidate must be an existing absolute version directory.", nameof(nextVersion));
        token.ThrowIfCancellationRequested();
        await RequireInstallationAsync(installationRoot, token);
        using var ownership = new FileStream(Path.Combine(installationRoot, "activation.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        string link = Path.Combine(installationRoot, "current");
        // Each switch, including a rollback to the same version, receives a new
        // pointer identity. An old retry cannot become valid again after a
        // forward/backward sequence (the stale-target ABA problem).
        string target = "selections/" + operationId.ToString("D");
        string selections = Path.Combine(installationRoot, "selections");
        string versionTarget = Path.GetRelativePath(selections, nextVersion);
        string receipt = Path.Combine(installationRoot, "operations", operationId.ToString("D") + ".json");
        UpdateActivation? previous = null;
        try
        {
            await using var file = new FileStream(receipt, FileMode.Open, FileAccess.Read, FileShare.Read);
            previous = await JsonSerializer.DeserializeAsync<UpdateActivation>(file, Json, token)
                ?? throw new InvalidDataException("The update operation receipt is missing.");
        }
        catch (FileNotFoundException) { }
        var operation = new UpdateActivation(operationId.ToString("D"), expectedTarget, target, versionTarget, "prepared");
        if (previous is not null && (previous.OperationId != operation.OperationId
            || previous.PreviousTarget != expectedTarget || previous.Target != target || previous.VersionTarget != versionTarget
            || previous.Status != "prepared"))
            throw new InvalidDataException("The update operation ID was reused with different arguments or an invalid receipt.");
        string current = ReadCurrent(link);
        if (previous is not null && current == target) return operation with { Status = "switched" };
        if (current != expectedTarget)
            throw new InvalidDataException("The active installation changed. Inspect it before retrying the update.");
        if (previous is null) await WriteNewAsync(receipt, operation, token);
        token.ThrowIfCancellationRequested();

        string selection = Path.Combine(installationRoot, target);
        if (File.Exists(selection) || Directory.Exists(selection) || new FileInfo(selection).LinkTarget is not null)
        {
            if (new FileInfo(selection).LinkTarget != versionTarget)
                throw new InvalidDataException("The update operation has a different version selection.");
        }
        else Directory.CreateSymbolicLink(selection, versionTarget);

        // The prepared receipt is flushed before the single pointer transition.
        // Recovery derives the outcome from current, including a lost reply.
        string pending = Path.Combine(installationRoot, "current-" + operationId.ToString("D") + ".pending");
        if (File.Exists(pending) || Directory.Exists(pending) || new FileInfo(pending).LinkTarget is not null)
        {
            if (new FileInfo(pending).LinkTarget != target)
                throw new InvalidDataException("An interrupted update left a different pending link.");
        }
        else Directory.CreateSymbolicLink(pending, target);
        beforeSwitch?.Invoke();
        token.ThrowIfCancellationRequested();
        // File.Move rejects directory symlinks before reaching rename on Unix.
        // Both links are in the same managed directory; rename changes the link
        // itself atomically and never overwrites the selected version's files.
        if (Rename(pending, link) != 0)
            throw new IOException("Cannot atomically switch the active installation link.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        return operation with { Status = "switched" };
    }

    public static string InspectTarget(string installationRoot)
    {
        RequireLinux();
        RequireRoot(installationRoot);
        return ReadCurrent(Path.Combine(installationRoot, "current"));
    }

    private static string ReadCurrent(string path) => new FileInfo(path).LinkTarget
        ?? throw new InvalidDataException("The managed current pointer is missing or is not a symbolic link.");

    private static void RequireRoot(string path)
    {
        if (!Path.IsPathFullyQualified(path) || Path.TrimEndingDirectorySeparator(path) == Path.GetPathRoot(path))
            throw new ArgumentException("Use an absolute dedicated installation directory, never a filesystem root.", nameof(path));
    }

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("This installation switch is Linux-specific.");
    }

    private static async Task RequireInstallationAsync(string root, CancellationToken token)
    {
        await using var file = new FileStream(Path.Combine(root, "installation.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 1 or > 4096) throw new InvalidDataException("Invalid installation identity size.");
        using var document = await JsonDocument.ParseAsync(file, cancellationToken: token);
        var value = document.RootElement;
        if (!value.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1
            || !value.TryGetProperty("product", out var product) || product.GetString() != "kicad-codex"
            || !value.TryGetProperty("installationId", out var id) || !Guid.TryParse(id.GetString(), out var parsed) || parsed == Guid.Empty
            || !Directory.Exists(Path.Combine(root, "operations")) || !Directory.Exists(Path.Combine(root, "selections")))
            throw new InvalidDataException("The directory is not a managed KiCad installation.");
    }

    private static async Task WriteNewAsync<T>(string path, T value, CancellationToken token)
    {
        string pending = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        bool ownsPending = false;
        try
        {
            await using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsPending = true;
                await JsonSerializer.SerializeAsync(file, value, Json, token);
                await file.FlushAsync(token);
                file.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(pending, path, overwrite: false);
            ownsPending = false;
        }
        finally { if (ownsPending) File.Delete(pending); }
    }

    [DllImport("libc", EntryPoint = "rename", SetLastError = true)]
    private static extern int Rename([MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);
}
