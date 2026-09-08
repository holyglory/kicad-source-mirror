using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace KiCad.Automation.Distribution;

public sealed record UpdateArtifact(string Platform, string Format, string FileName, long Bytes, string Sha256);
public sealed record UpdateRelease(int SchemaVersion, string Product, string Channel, long Sequence,
    string Version, string Commit, IReadOnlyList<UpdateArtifact> Artifacts);
public sealed record AcceptedUpdateManifest(long Sequence, string PayloadSha256);
internal sealed record SignedUpdateEnvelope(int SchemaVersion, string Payload, string Signature);

public sealed class VerifiedUpdateManifest
{
    private readonly byte[] envelope;
    public UpdateRelease Release { get; }
    public string PayloadSha256 { get; }
    public string PublisherKeySha256 { get; }
    internal VerifiedUpdateManifest(UpdateRelease release, string digest, string keyDigest, ReadOnlyMemory<byte> signedEnvelope = default)
    {
        envelope = signedEnvelope.ToArray();
        Release = release with { Artifacts = Array.AsReadOnly(release.Artifacts.ToArray()) };
        PayloadSha256 = digest;
        PublisherKeySha256 = keyDigest;
    }

    public byte[] CopyEnvelope() => (byte[])envelope.Clone();

    public UpdateArtifact? ForInstallation(string platform, string format)
    {
        UpdateManifestCodec.RequirePlatform(platform);
        if (!UpdateManifestCodec.CompatibleFormat(platform, format, "package." + format))
            throw new InvalidDataException("Unsupported update installation format.");
        return Release.Artifacts.SingleOrDefault(artifact => artifact.Platform == platform && artifact.Format == format);
    }
}

public static partial class UpdateManifestCodec
{
    public const int MaximumPayloadBytes = 256 * 1024;
    public const int MaximumEnvelopeBytes = 512 * 1024;
    public const long MaximumArtifactBytes = 16L * 1024 * 1024 * 1024;
    private const string P256Oid = "1.2.840.10045.3.1.7";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false, MaxDepth = 16 };

    public static byte[] Sign(UpdateRelease release, ECDsa publisher)
    {
        RequireKey(publisher);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(release, Json);
        _ = ReadRelease(payload);
        byte[] signature = publisher.SignData(payload, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return JsonSerializer.SerializeToUtf8Bytes(new SignedUpdateEnvelope(1,
            Convert.ToBase64String(payload), Convert.ToBase64String(signature)), Json);
    }

    public static VerifiedUpdateManifest Verify(ReadOnlyMemory<byte> envelopeBytes,
        ReadOnlySpan<byte> trustedPublisherSpki, string expectedChannel, AcceptedUpdateManifest? accepted = null)
    {
        RequireChannel(expectedChannel);
        if (envelopeBytes.Length == 0 || envelopeBytes.Length > MaximumEnvelopeBytes)
            throw new InvalidDataException("Update envelope size is invalid.");
        envelopeBytes = envelopeBytes.ToArray();
        RejectDuplicateProperties(envelopeBytes);
        SignedUpdateEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<SignedUpdateEnvelope>(envelopeBytes.Span, Json)
                ?? throw new InvalidDataException("Update envelope is missing.");
        }
        catch (JsonException error) { throw new InvalidDataException("Update envelope is invalid.", error); }
        if (envelope.SchemaVersion != 1 || envelope.Payload is null || envelope.Signature is null)
            throw new InvalidDataException("Unsupported or incomplete update envelope.");
        byte[] payload, signature;
        try { payload = Convert.FromBase64String(envelope.Payload); signature = Convert.FromBase64String(envelope.Signature); }
        catch (FormatException error) { throw new InvalidDataException("Update envelope encoding is invalid.", error); }
        if (payload.Length == 0 || payload.Length > MaximumPayloadBytes || signature.Length != 64)
            throw new InvalidDataException("Update payload or signature size is invalid.");
        using var publisher = ECDsa.Create();
        try
        {
            publisher.ImportSubjectPublicKeyInfo(trustedPublisherSpki, out int consumed);
            if (consumed != trustedPublisherSpki.Length) throw new InvalidDataException("Publisher key has trailing data.");
            RequireKey(publisher);
            if (!publisher.VerifyData(payload, signature, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new InvalidDataException("Update signature does not match the pinned publisher.");
        }
        catch (CryptographicException error) { throw new InvalidDataException("Pinned update publisher key is invalid.", error); }
        UpdateRelease release = ReadRelease(payload);
        if (release.Channel != expectedChannel) throw new InvalidDataException("Update channel does not match.");
        string digest = Convert.ToHexStringLower(SHA256.HashData(payload));
        if (accepted is not null)
        {
            if (accepted.Sequence < 1 || !HashPattern().IsMatch(accepted.PayloadSha256 ?? ""))
                throw new ArgumentException("The saved update checkpoint is invalid.", nameof(accepted));
            if (release.Sequence < accepted.Sequence)
                throw new InvalidDataException("Update manifest predates the accepted checkpoint.");
            if (release.Sequence == accepted.Sequence && digest != accepted.PayloadSha256)
                throw new InvalidDataException("Different update content reused an accepted sequence.");
        }
        return new(release, digest, Convert.ToHexStringLower(SHA256.HashData(trustedPublisherSpki)), envelopeBytes);
    }

    private static UpdateRelease ReadRelease(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length == 0 || payload.Length > MaximumPayloadBytes)
            throw new InvalidDataException("Update payload size is invalid.");
        RejectDuplicateProperties(payload);
        UpdateRelease release;
        try
        {
            release = JsonSerializer.Deserialize<UpdateRelease>(payload.Span, Json)
                ?? throw new InvalidDataException("Update release is missing.");
        }
        catch (JsonException error) { throw new InvalidDataException("Update release is invalid.", error); }
        if (release.SchemaVersion != 1 || release.Product != "kicad-codex" || release.Sequence < 1
            || !VersionPattern().IsMatch(release.Version ?? "") || !CommitPattern().IsMatch(release.Commit ?? "")
            || release.Artifacts is null || release.Artifacts.Count is < 1 or > 4)
            throw new InvalidDataException("Update release identity is invalid.");
        RequireChannel(release.Channel);
        var platforms = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in release.Artifacts)
        {
            if (artifact is null) throw new InvalidDataException("Update artifact is missing.");
            RequirePlatform(artifact.Platform);
            if (artifact.FileName is null || !platforms.Add(artifact.Platform + "/" + artifact.Format) || !names.Add(artifact.FileName)
                || artifact.Bytes is < 1 or > MaximumArtifactBytes
                || !HashPattern().IsMatch(artifact.Sha256 ?? "") || !FilePattern().IsMatch(artifact.FileName ?? "")
                || !CompatibleFormat(artifact.Platform, artifact.Format, artifact.FileName!))
                throw new InvalidDataException("Update artifact identity, format or size is invalid.");
        }
        return release;
    }

    internal static void RequirePlatform(string platform)
    {
        if (platform is not ("linux-x64" or "osx-arm64" or "osx-x64"))
            throw new InvalidDataException("Unsupported update platform.");
    }

    private static void RequireChannel(string channel)
    {
        if (channel is not ("preview" or "stable")) throw new InvalidDataException("Unsupported update channel.");
    }

    internal static bool CompatibleFormat(string platform, string format, string name) =>
        (platform == "linux-x64" ? format is "tar.gz" or "deb" : format == "zip")
        && name.EndsWith("." + format, StringComparison.Ordinal);

    private static void RequireKey(ECDsa publisher)
    {
        if (publisher.ExportParameters(false).Curve.Oid.Value != P256Oid)
            throw new InvalidDataException("Updates require the pinned P-256 publisher key.");
    }

    private static void RejectDuplicateProperties(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            Visit(document.RootElement);
        }
        catch (JsonException error) { throw new InvalidDataException("Update JSON is invalid.", error); }
        static void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new InvalidDataException("Repeated update JSON property.");
                    Visit(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var item in element.EnumerateArray()) Visit(item);
        }
    }

    [GeneratedRegex(@"\A[0-9a-f]{64}\z")] private static partial Regex HashPattern();
    [GeneratedRegex(@"\A[0-9a-f]{40}\z")] private static partial Regex CommitPattern();
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9.-]{0,79}\z")] private static partial Regex VersionPattern();
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]*\.(?:tar\.gz|deb|zip)\z")] private static partial Regex FilePattern();
}
