using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

public sealed record SchematicSyncState(Guid OriginId, DocumentRevision NativeRevision,
    bool TrackingComplete, SchematicScreenData Baseline, SchematicScreenData Xml, SchematicScreenData Native,
    SchematicSyncResolution? Resolution = null);
public sealed record SchematicSyncResolution(string SnapshotToken,
    Dictionary<Guid, SchematicConflictChoice> Choices,
    Dictionary<string, SchematicConflictChoice>? NetChainChoices = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Dictionary<string, SchematicConflictChoice>? VariantChoices = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Dictionary<string, SchematicConflictChoice>? CacheChoices = null);
public sealed record StoredSchematicSyncState(string RevisionToken, SchematicSyncState State);

/// <summary>Local synchronization recovery data, not the engineering source or
/// completion ledger. Stores all three versions; never edits a native document.</summary>
public sealed class SchematicSyncStore(string statePath)
{
    private readonly string path = Path.GetFullPath(statePath);
    private static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private sealed record Envelope([property: JsonRequired] int Version,
        [property: JsonRequired] Guid OriginId, [property: JsonRequired] string NativeEpoch,
        [property: JsonRequired] ulong NativeSequence, [property: JsonRequired] bool TrackingComplete,
        [property: JsonRequired] string BaselineXml, [property: JsonRequired] string DesiredXml,
        [property: JsonRequired] string NativeXml,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SchematicSyncResolution? Resolution = null);

    /// <summary>Persist choices separately from the three original versions.
    /// The checkpoint token guards concurrent writers; the snapshot token binds
    /// choices to their exact content and native session. No native edit occurs.</summary>
    public StoredSchematicSyncState Resolve(string expectedRevisionToken,
        IReadOnlyDictionary<Guid, SchematicConflictChoice> choices,
        IReadOnlyDictionary<string, SchematicConflictChoice>? netChainChoices = null,
        IReadOnlyDictionary<string, SchematicConflictChoice>? variantChoices = null,
        IReadOnlyDictionary<string, SchematicConflictChoice>? cacheChoices = null)
    {
        var current = Read();
        if (current is null || current.RevisionToken != expectedRevisionToken)
            throw Failure("sync_store_changed", "Synchronization state changed; reload conflicts before choosing.");
        var combined = current.State.Resolution?.Choices.ToDictionary(p => p.Key, p => p.Value) ?? [];
        foreach (var (id, choice) in choices) combined[id] = choice;
        var chains = current.State.Resolution?.NetChainChoices?.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal) ?? [];
        foreach (var (name, choice) in netChainChoices ?? new Dictionary<string, SchematicConflictChoice>()) chains[name] = choice;
        var variants = current.State.Resolution?.VariantChoices?.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal) ?? [];
        foreach (var (name, choice) in variantChoices ?? new Dictionary<string, SchematicConflictChoice>()) variants[name] = choice;
        var caches = current.State.Resolution?.CacheChoices?.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal) ?? [];
        foreach (var (key, choice) in cacheChoices ?? new Dictionary<string, SchematicConflictChoice>()) caches[key] = choice;
        var next = current.State with { Resolution = new(SnapshotToken(current.State), combined,
            chains.Count == 0 ? null : chains, variants.Count == 0 ? null : variants, caches.Count == 0 ? null : caches) };
        return Save(next, expectedRevisionToken);
    }

    public static SchematicItemMergeResult Plan(SchematicSyncState state)
    {
        Validate(state);
        return state.Resolution is { } resolution
            ? SchematicItemMerge.Resolve(state.Baseline, state.Xml, state.Native, resolution.Choices, resolution.NetChainChoices, resolution.VariantChoices, resolution.CacheChoices)
            : SchematicItemMerge.Plan(state.Baseline, state.Xml, state.Native);
    }

    public StoredSchematicSyncState? Read()
    {
        try { return ReadCore(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw Failure("sync_store_io", error.Message); }
    }

    public StoredSchematicSyncState Save(SchematicSyncState state, string? expectedRevisionToken)
    {
        Validate(state);
        byte[] bytes = Encode(state);
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Keep the lock file in place. Unlinking it can let a second writer
            // acquire a different file while a first writer still owns the old one.
            // SA-01/04: local trusted-session coordination, no new account boundary.
            using var ownership = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var current = ReadCore();
            if (current?.RevisionToken != expectedRevisionToken)
                throw Failure("sync_store_changed", "Synchronization state changed; reload it before saving.");
            if (current?.RevisionToken == Token(bytes)) return current;
            temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            // Same-directory replacement: failed writes never truncate the last
            // valid checkpoint. This is not a whole-system power-loss guarantee.
            File.Move(temporary, path, overwrite: true);
            temporary = null;
            return Decode(bytes);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw Failure("sync_store_io", error.Message); }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (IOException) { /* Preserve an uncommitted temporary file if cleanup fails. */ }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private StoredSchematicSyncState? ReadCore()
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        return Decode(bytes);
    }

    private static StoredSchematicSyncState Decode(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || document.RootElement.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count()
                    != document.RootElement.EnumerateObject().Count())
                throw Failure("invalid_sync_store", "Invalid or duplicate synchronization record fields.");
            var envelope = JsonSerializer.Deserialize<Envelope>(bytes, Json)
                ?? throw Failure("invalid_sync_store", "Missing synchronization state.");
            if (envelope.Version != 1) throw Failure("invalid_sync_store", "Unsupported synchronization state version.");
            var state = new SchematicSyncState(envelope.OriginId,
                new(envelope.NativeEpoch, envelope.NativeSequence), envelope.TrackingComplete,
                Screen(envelope.BaselineXml), Screen(envelope.DesiredXml), Screen(envelope.NativeXml), envelope.Resolution);
            Validate(state);
            return new(Token(bytes), state);
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        { throw Failure("invalid_sync_store", error.Message); }
    }

    private static SchematicScreenData Screen(string xml) => SchematicDataXml.Read(xml) as SchematicScreenData
        ?? throw Failure("invalid_sync_store", "Expected a typed schematic screen record.");
    private static void Validate(SchematicSyncState state)
    {
        if (state.OriginId == Guid.Empty || state.NativeRevision is null || string.IsNullOrWhiteSpace(state.NativeRevision.Epoch))
            throw Failure("invalid_sync_store", "An origin identity and native process epoch are required.");
        var metadata = state.Baseline?.Metadata;
        if (metadata?.Document?.SheetPath is not { } sheet || sheet.Path.Count == 0
            || !Guid.TryParseExact(metadata.ScreenId?.Value, "D", out var screenId) || screenId == Guid.Empty)
            throw Failure("invalid_sync_store", "An explicit screen and sheet-instance target is required.");
        if (sheet.Path.Any(id => !Guid.TryParseExact(id.Value, "D", out var value) || value == Guid.Empty))
            throw Failure("invalid_sync_store", "Invalid sheet-instance identity.");
        foreach (var version in new[] { state.Xml, state.Native })
            if (version?.Metadata is not { } other || !metadata.ScreenId.Equals(other.ScreenId)
                || !metadata.Document.Equals(other.Document))
                throw Failure("invalid_sync_store", "Synchronization versions must identify the same screen and sheet instance.");
        if (state.Resolution is { } resolution)
        {
            if (resolution.Choices is null || resolution.SnapshotToken != SnapshotToken(state))
                throw Failure("stale_sync_resolution", "Conflict choices do not match this content and native session; reobserve and choose again.");
            SchematicItemMerge.Resolve(state.Baseline!, state.Xml, state.Native, resolution.Choices, resolution.NetChainChoices, resolution.VariantChoices, resolution.CacheChoices);
        }
    }
    private static byte[] Encode(SchematicSyncState state) => JsonSerializer.SerializeToUtf8Bytes(
        new Envelope(1, state.OriginId, state.NativeRevision.Epoch, state.NativeRevision.Sequence,
            state.TrackingComplete, SchematicDataXml.Write(state.Baseline), SchematicDataXml.Write(state.Xml),
            SchematicDataXml.Write(state.Native), state.Resolution is { } resolution
                ? resolution with { Choices = resolution.Choices.OrderBy(p => p.Key).ToDictionary(p => p.Key, p => p.Value),
                    NetChainChoices = resolution.NetChainChoices?.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value),
                    VariantChoices = resolution.VariantChoices?.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value),
                    CacheChoices = resolution.CacheChoices?.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value) } : null), Json);
    private static string SnapshotToken(SchematicSyncState state) => Token(Encode(state with { Resolution = null }));
    private static string Token(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static AutomationException Failure(string code, string message) => new(code, message);
}
