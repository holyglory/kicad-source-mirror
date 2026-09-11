using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using KiCad.Automation.Distribution;

namespace KiCad.Automation.Downloads;

public sealed class DownloadPage
{
    private readonly DownloadCatalogue catalogue;
    private readonly SignedUpdateCatalogue updates;
    private readonly Dictionary<(string Platform, string Theme), byte[]> pages = new();

    public DownloadPage(DownloadCatalogue catalogue, SignedUpdateCatalogue updates)
    {
        this.catalogue = catalogue;
        this.updates = updates;
        // The verified catalogue is immutable for this deployment. Prepare the
        // finite variants once; never cache arbitrary request keys or user data.
        foreach (string platform in Platforms.Concat(["mac", ""]))
            foreach (string theme in new[] { "", "light", "dark" })
                pages.Add((platform, theme), System.Text.Encoding.UTF8.GetBytes(RenderCore(platform, theme)));
    }
    private const string Repository = "https://github.com/holyglory/KAICad";
    public static string DetectPlatform(string userAgent, string hintPlatform = "", string hintArchitecture = "")
    {
        string ua = userAgent.ToLowerInvariant(), platform = hintPlatform.Trim('"').ToLowerInvariant();
        string arch = hintArchitecture.Trim('"').ToLowerInvariant();
        if (ua.Contains("android") || ua.Contains("iphone") || ua.Contains("ipad") || ua.Contains("mobile")) return "";
        if (platform == "macos" || ua.Contains("macintosh"))
            return arch == "arm" ? "osx-arm64" : arch == "x86" ? "osx-x64" : "mac";
        if (platform == "windows" || ua.Contains("windows nt"))
            return arch == "arm" || ua.Contains("arm64") ? "" : ua.Contains("win64") || ua.Contains("x64") || arch == "x86" ? "win-x64" : "";
        if ((platform == "linux" || ua.Contains("linux")) && (ua.Contains("x86_64") || arch == "x86")) return "linux-x64";
        return "";
    }

    public DownloadArtifact? Latest(string platform)
    {
        SignedUpdateFeed? feed;
        bool found = platform == "linux-x64" ? updates.TryGet("preview", out feed) : updates.TryGet(platform, "preview", out feed);
        if (found)
        {
            var file = feed!.Manifest.Release.Artifacts.FirstOrDefault(x => x.Platform == platform && x.Format is "zip" or "tar.gz");
            if (file is not null && catalogue.Files.TryGetValue(file.FileName, out var current)) return current.Artifact;
        }
        return catalogue.Manifest.Artifacts.Where(x => x.Platform == platform && !x.FileName.EndsWith(".deb", StringComparison.Ordinal))
            .OrderByDescending(x => x.Version, StringComparer.Ordinal).ThenBy(x => x.FileName, StringComparer.Ordinal).FirstOrDefault();
    }

    public byte[] Render(HttpRequest request)
    {
        string selected = request.Query["platform"].ToString();
        if (!Platforms.Contains(selected)) selected = DetectPlatform(request.Headers.UserAgent.ToString(),
            request.Headers["Sec-CH-UA-Platform"].ToString(), request.Headers["Sec-CH-UA-Arch"].ToString());
        string theme = request.Query["theme"] is var value && value == "light" ? "light" : value == "dark" ? "dark" : "";
        return pages[(selected, theme)];
    }

    private string RenderCore(string selected, string theme)
    {
        var latest = Platforms.ToDictionary(x => x, Latest);
        string choice = selected == "mac" ? "<p class=chip-hint>Choose your Mac’s chip</p><div class=mac-choice>" + Button(latest["osx-arm64"], "Apple Silicon", "primary") + Button(latest["osx-x64"], "Intel", "outline") + "</div>"
            : latest.TryGetValue(selected, out var recommended) && recommended is not null
                ? Button(recommended, "Download for " + OsName(selected), "primary", Icon(selected)) + "<p class=download-meta>" + Architecture(selected) + " · " + Size(recommended.Bytes) + " · Preview</p>"
                : "<a class=\"primary button\" href=#downloads>Choose your download</a><p class=download-meta>Desktop builds for macOS, Windows and Linux</p>";
        string cards = Card("mac", "macOS", ["osx-arm64", "osx-x64"]) + Card("win-x64", "Windows", ["win-x64"])
            + Card("linux-x64", "Linux", ["linux-x64"])
            + "<article class=platform-card>" + Icon("history") + "<h3>Previous releases</h3><a class=\"button outline\" href=#releases>View all builds</a></article>";
        var currentFiles = latest.Values.Where(x => x is not null).Select(x => x!.FileName).ToHashSet(StringComparer.Ordinal);
        string rows = string.Join("", catalogue.Manifest.Artifacts.Where(x => x.Platform != "source")
            .OrderByDescending(x => currentFiles.Contains(x.FileName)).ThenBy(x => x.Platform, StringComparer.Ordinal)
            .ThenByDescending(x => x.Version, StringComparer.Ordinal).Select(x =>
                $"<div class=release-row data-platform=\"{x.Platform}\"><div><strong>{E(OsName(x.Platform))} · {E(Architecture(x.Platform))}</strong><p>{E(x.Version)}{(currentFiles.Contains(x.FileName) ? " <span class=current>Current</span>" : "")}</p></div><span class=release-size>{Size(x.Bytes)}</span><div class=release-actions>{Button(x, x.FileName.EndsWith(".deb") ? "Download .deb" : "Download", "outline")}{Source(x)}</div></div>"));
        var data = latest.Where(x => x.Value is not null).ToDictionary(x => x.Key, x => new { url = Url(x.Value!), os = OsName(x.Key), architecture = Architecture(x.Key), size = Size(x.Value!.Bytes) });
        return Template.Replace("{{THEME}}", theme).Replace("{{CHOICE}}", choice).Replace("{{CARDS}}", cards)
            .Replace("{{ROWS}}", rows).Replace("{{DATA}}", JsonSerializer.Serialize(data)).Replace("{{REPO}}", Repository);

        string Card(string icon, string title, string[] targets) => "<article class=platform-card>" + Icon(icon) + "<h3>" + title + "</h3>"
            + string.Join("", targets.Select(p => Button(latest[p], Architecture(p), "outline"))) + "</article>";
    }

    private static readonly string[] Platforms = ["osx-arm64", "osx-x64", "win-x64", "linux-x64"];
    private static string OsName(string p) => p.StartsWith("osx-", StringComparison.Ordinal) ? "macOS" : p == "win-x64" ? "Windows" : "Linux";
    private static string Architecture(string p) => p == "osx-arm64" ? "Apple Silicon" : p == "osx-x64" ? "Intel (x64)" : "x64";
    private static string E(string text) => WebUtility.HtmlEncode(text);
    private static string Size(long bytes) => (bytes / 1048576d).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
    private static string Url(DownloadArtifact file) => "/artifacts/" + Uri.EscapeDataString(file.FileName);
    private static string Icon(string p) => $"<img class=icon src=\"/site/{(p.StartsWith("osx-") || p == "mac" ? "apple" : p == "win-x64" ? "windows" : p == "linux-x64" ? "linux" : p)}.svg\" alt=\"\" width=24 height=24>";
    private static string Button(DownloadArtifact? file, string label, string style, string icon = "") => file is null
        ? "<span class=unavailable>Build unavailable</span>"
        : $"<a class=\"button {style}\" href=\"{Url(file)}\" download aria-label=\"{E(label + " — " + file.Version)}\">{icon}<span>{E(label)}</span>{Icon("download")}</a>";
    private string Source(DownloadArtifact file)
    {
        var source = catalogue.Manifest.Artifacts.FirstOrDefault(x => x.Platform == "source" && x.Sha256 == file.SourceSha256);
        return source is null ? "" : $"<a class=source-download href=\"{Url(source)}\" download aria-label=\"Source for {E(file.Version)}\">Source</a>";
    }
    private static readonly Assembly Assembly = typeof(DownloadPage).Assembly;
    private static byte[] Read(string name)
    {
        using var input = Assembly.GetManifestResourceStream("kicad-downloads.Web." + name) ?? throw new InvalidOperationException("Missing web asset: " + name);
        using var output = new MemoryStream(); input.CopyTo(output); return output.ToArray();
    }
    private static readonly string Template = System.Text.Encoding.UTF8.GetString(Read("index.html"));
    private static readonly Dictionary<string, (byte[] Bytes, string Type)> Assets = new(StringComparer.Ordinal);
    public static IResult Asset(string name)
    {
        // Public assets are compiled resources, never arbitrary files under a workspace.
        string[] allowed = ["site.css", "site.js", "theme.js", "hero-dark.png", "hero-light.png", "inter.woff2", "apple.svg", "windows.svg", "linux.svg", "github.svg", "download.svg", "history.svg", "moon.svg", "sun.svg"];
        if (!allowed.Contains(name, StringComparer.Ordinal)) return Results.NotFound();
        lock (Assets)
        {
            if (!Assets.TryGetValue(name, out var asset))
            {
                string type = name.EndsWith(".css") ? "text/css" : name.EndsWith(".js") ? "text/javascript" : name.EndsWith(".svg") ? "image/svg+xml" : name.EndsWith(".woff2") ? "font/woff2" : "image/png";
                Assets.Add(name, asset = (Read(name), type));
            }
            return Results.Bytes(asset.Bytes, asset.Type);
        }
    }
}
