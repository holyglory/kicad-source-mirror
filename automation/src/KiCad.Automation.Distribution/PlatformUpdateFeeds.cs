namespace KiCad.Automation.Distribution;

/// <summary>Native feed namespaces on the existing publisher, not new services or trust identities.</summary>
public static class PlatformUpdateFeeds
{
    public static IReadOnlyList<string> NativePlatforms { get; } = Array.AsReadOnly(new[] { "osx-arm64", "osx-x64", "win-x64" });
    public static bool IsNativePlatform(string platform) => NativePlatforms.Contains(platform, StringComparer.Ordinal);
    public static string RelativeFeed(string platform, string channel)
    {
        if (!IsNativePlatform(platform) || channel is not ("preview" or "stable"))
            throw new ArgumentException("Select a supported native platform and update channel.");
        return "updates/platforms/" + platform + "/" + channel + ".json";
    }
    public static Uri PublisherBase(Uri root, string platform)
    {
        if (!IsNativePlatform(platform)) throw new ArgumentException("Unsupported native update platform.");
        using var validation = new UpdateDownloader(root);
        return new Uri(root, "platforms/" + platform + "/");
    }
    public static bool IsFeedPath(string path)
    {
        if (path is "updates/preview.json" or "updates/stable.json") return true;
        string[] parts = path.Split('/');
        return parts.Length == 4 && parts[0] == "updates" && parts[1] == "platforms"
            && IsNativePlatform(parts[2]) && parts[3] is "preview.json" or "stable.json";
    }

    public static void RequireLegacyCompatible(VerifiedUpdateManifest manifest)
    {
        // Existing Linux installations must still parse the complete root feed,
        // including records for targets they do not themselves install.
        if (manifest.Release.Artifacts.Any(x => x.Platform == "win-x64"
            || (x.Platform is "osx-arm64" or "osx-x64" && x.Format != "zip")))
            throw new InvalidDataException("Use a native platform feed for records unsupported by legacy Linux clients.");
    }
}
