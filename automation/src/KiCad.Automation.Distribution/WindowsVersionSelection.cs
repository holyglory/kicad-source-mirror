using System.Text.Json;
using System.Text.Json.Serialization;

namespace KiCad.Automation.Distribution;

internal sealed record WindowsSelectedVersion(int SchemaVersion, string SelectionId, string VersionTarget);
internal sealed record WindowsSelectionIntent(int SchemaVersion, string OperationId, WindowsSelectedVersion Previous, WindowsSelectedVersion Next);

/// <summary>Internal record transition only. Callers must authenticate retained
/// payloads and obtain the explicit user update action before activation.</summary>
internal static class WindowsVersionSelection
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false, MaxDepth = 6 };

    public static async Task<WindowsSelectedVersion> InitializeAsync(string manager, string versionDigest, CancellationToken token)
    {
        RequireManagerPath(manager);
        string root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(manager))!;
        RequireOrdinary(root, directory: true);
        string creationLock = Path.Combine(root, "manager-initialize.lock");
        if (File.Exists(creationLock)) RequireOrdinary(creationLock, directory: false);
        using var ownership = new FileStream(creationLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (Directory.Exists(manager) || File.Exists(manager)) throw new IOException("The selection directory must be new.");
        string version = RequirePayload(manager, versionDigest);
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(manager); Directory.CreateDirectory(Path.Combine(manager, "operations"));
        var initial = new WindowsSelectedVersion(1, Guid.NewGuid().ToString("D"), version);
        try { await WriteNew(Path.Combine(manager, "current.json"), initial, token); return initial; }
        catch { Directory.Delete(manager, recursive: true); throw; }
    }

    public static WindowsSelectedVersion Inspect(string manager)
    {
        RequireManager(manager);
        var selected = Read<WindowsSelectedVersion>(Path.Combine(manager, "current.json"));
        ValidateSelection(manager, selected);
        return selected;
    }

    internal static WindowsSelectionIntent InspectIntent(string manager, Guid operationId)
    {
        RequireManager(manager);
        if (operationId == Guid.Empty) throw new ArgumentException("An update operation identity is required.");
        var intent = Read<WindowsSelectionIntent>(Path.Combine(manager, "operations", operationId.ToString("D") + ".json"));
        if (intent.SchemaVersion != 1 || intent.OperationId != operationId.ToString("D") || intent.Previous is null || intent.Next is null
            || intent.Next.SelectionId != intent.OperationId)
            throw new InvalidDataException("Invalid Windows selection intent.");
        ValidateSelection(manager, intent.Previous); ValidateSelection(manager, intent.Next);
        return intent;
    }

    public static async Task<WindowsSelectedVersion> SwitchAsync(string manager, WindowsSelectedVersion expected,
        string nextDigest, Guid operationId, CancellationToken token, Action? beforeSwitch = null)
    {
        RequireManager(manager);
        if (operationId == Guid.Empty) throw new ArgumentException("An update operation identity is required.");
        ValidateSelection(manager, expected);
        var next = new WindowsSelectedVersion(1, operationId.ToString("D"), RequirePayload(manager, nextDigest));
        var intent = new WindowsSelectionIntent(1, next.SelectionId, expected, next);
        token.ThrowIfCancellationRequested();
        string lockPath = Path.Combine(manager, "activation.lock");
        if (File.Exists(lockPath)) RequireOrdinary(lockPath, directory: false);
        using var ownership = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string receipt = Path.Combine(manager, "operations", next.SelectionId + ".json");
        bool exists = File.Exists(receipt);
        if (exists && Read<WindowsSelectionIntent>(receipt) != intent)
            throw new InvalidDataException("The update operation was reused with different arguments.");
        var current = Inspect(manager);
        if (exists && current == next) return next;
        if (current != expected) throw new InvalidDataException("The active Windows selection changed; inspect before retrying.");
        if (!exists) await WriteNew(receipt, intent, token);
        string pending = Path.Combine(manager, "current-" + next.SelectionId + ".pending");
        if (File.Exists(pending))
        {
            if (Read<WindowsSelectedVersion>(pending) != next)
                throw new InvalidDataException("An interrupted Windows selection has different pending data.");
        }
        else await WriteNew(pending, next, token);
        beforeSwitch?.Invoke();
        token.ThrowIfCancellationRequested();
        // No native application file is replaced, and no version is removed.
        // File.Replace uses Windows ReplaceFile on this same-volume record.
        File.Replace(pending, Path.Combine(manager, "current.json"), null);
        return next;
    }

    private static string RequirePayload(string manager, string digest)
    {
        if (digest is null || digest.Length != 64 || digest.Any(character => !char.IsAsciiHexDigitLower(character)))
            throw new InvalidDataException("A retained version digest is required.");
        string root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(manager))!;
        foreach (string path in new[] { root, Path.Combine(root, "versions"), Path.Combine(root, "versions", digest), Path.Combine(root, "versions", digest, "payload") })
            RequireOrdinary(path, directory: true);
        return "versions/" + digest + "/payload";
    }

    private static void ValidateSelection(string manager, WindowsSelectedVersion selected)
    {
        if (selected.SchemaVersion != 1 || !Guid.TryParseExact(selected.SelectionId, "D", out var id) || id == Guid.Empty
            || selected.VersionTarget is null || selected.VersionTarget.Length != 81
            || !selected.VersionTarget.StartsWith("versions/", StringComparison.Ordinal)
            || !selected.VersionTarget.EndsWith("/payload", StringComparison.Ordinal)
            || RequirePayload(manager, selected.VersionTarget[9..73]) != selected.VersionTarget)
            throw new InvalidDataException("Invalid retained Windows selection.");
    }

    private static void RequireManagerPath(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows version selection requires Windows.");
        if (!Path.IsPathFullyQualified(path) || Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) != "manager"
            || path.StartsWith("\\\\", StringComparison.Ordinal))
            throw new ArgumentException("Use the local dedicated installation manager directory.");
    }

    private static void RequireManager(string manager)
    {
        RequireManagerPath(manager); RequireOrdinary(manager, directory: true);
        RequireOrdinary(Path.Combine(manager, "operations"), directory: true);
    }

    private static void RequireOrdinary(string path, bool directory)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReparsePoint) || attributes.HasFlag(FileAttributes.Directory) != directory)
            throw new InvalidDataException("The Windows selection contains a redirected or mismatched entry.");
    }

    private static T Read<T>(string path)
    {
        RequireOrdinary(path, directory: false);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (file.Length is < 1 or > 4096) throw new InvalidDataException("Invalid Windows selection record size.");
        byte[] bytes = new byte[(int)file.Length]; file.ReadExactly(bytes);
        using var document = JsonDocument.Parse(bytes);
        RejectDuplicates(document.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("Missing Windows selection record.");
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid Windows selection record shape.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name)) throw new InvalidDataException("Repeated Windows selection property.");
            if (property.Value.ValueKind == JsonValueKind.Object) RejectDuplicates(property.Value);
        }
    }

    private static async Task WriteNew<T>(string path, T value, CancellationToken token)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        bool owns = false;
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                owns = true;
                await JsonSerializer.SerializeAsync(file, value, Json, token);
                await file.FlushAsync(token); file.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested(); File.Move(temporary, path, overwrite: false); owns = false;
        }
        finally { if (owns) File.Delete(temporary); }
    }
}
