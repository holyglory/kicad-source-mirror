using System.Security.Cryptography;

namespace KiCad.Automation.Distribution;

internal sealed record WindowsBootstrap(int SchemaVersion, string VersionDigest, string HelperSha256);

public static partial class WindowsVerifiedVersions
{
    /// <summary>Install retained versions and native root launchers into a new
    /// directory. Does not register OS shortcuts, replace another installation,
    /// start editors, or claim that automatic updating is qualified.</summary>
    public static Task<SelectedWindowsVersion> InstallAsync(string root, string archive,
        ReadOnlyMemory<byte> envelope, ReadOnlyMemory<byte> trustedPublisherSpki, Uri origin, string channel,
        CancellationToken token = default) => CreateCore(root, archive, envelope, trustedPublisherSpki, origin, channel,
            async (work, manifest) =>
            {
                string bin = Path.Combine(work, "versions", manifest.PayloadSha256, "payload/bin");
                foreach (var (source, destination) in new[] { ("kicad-automation-launcher.exe", "kicad.exe"),
                    ("kicad-automation-mcp-launcher.exe", "kicad-mcp.exe") })
                {
                    string from = Path.Combine(bin, source);
                    Ordinary(from, directory: false); WindowsUpdateStager.RequireX64(from);
                    File.Copy(from, Path.Combine(work, destination), overwrite: false);
                }
                await using var helper = File.OpenRead(Path.Combine(bin, "kicad-mcp.exe"));
                var bootstrap = new WindowsBootstrap(1, manifest.PayloadSha256,
                    Convert.ToHexStringLower(await SHA256.HashDataAsync(helper, token)));
                await WriteNew(Path.Combine(work, "launcher-bootstrap.json"), bootstrap, token);
                await WriteNew(Path.Combine(work, "installed.json"), new
                { schemaVersion = 1, status = "installed", automaticUpdatingQualified = false, nativeEditorRestarted = false }, token);
            }, token);

    public static async Task ValidateBootstrapAsync(string root, CancellationToken token = default)
    {
        root = Root(root);
        var bootstrap = await ReadJson<WindowsBootstrap>(Path.Combine(root, "launcher-bootstrap.json"), 4096, token);
        if (bootstrap.SchemaVersion != 1 || !Digest(bootstrap.VersionDigest) || !Digest(bootstrap.HelperSha256))
            throw new InvalidDataException("Invalid Windows bootstrap metadata.");
        var manifest = await ReadVersion(root, bootstrap.VersionDigest, await Policy(root, token), token);
        var version = Describe(root, manifest);
        await using var helper = File.OpenRead(version.McpExecutable);
        if (Convert.ToHexStringLower(await SHA256.HashDataAsync(helper, token)) != bootstrap.HelperSha256)
            throw new InvalidDataException("The installed Windows bootstrap helper changed.");
        foreach (var (source, target) in new[] { ("kicad-automation-launcher.exe", "kicad.exe"),
            ("kicad-automation-mcp-launcher.exe", "kicad-mcp.exe") })
        {
            string launcher = Path.Combine(root, target); Ordinary(launcher, directory: false);
            await using var expected = File.OpenRead(Path.Combine(version.VersionDirectory, "bin", source));
            await using var actual = File.OpenRead(launcher);
            byte[] expectedHash = await SHA256.HashDataAsync(expected, token), actualHash = await SHA256.HashDataAsync(actual, token);
            if (expected.Length != actual.Length || !expectedHash.AsSpan().SequenceEqual(actualHash))
                throw new InvalidDataException("A Windows root launcher changed after installation.");
        }
    }
}
