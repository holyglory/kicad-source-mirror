using Microsoft.Net.Http.Headers;
using KiCad.Automation.Distribution;

namespace KiCad.Automation.Downloads;

public static class DownloadServer
{
    public static WebApplication Create(DownloadCatalogue catalogue, string url, SignedUpdateCatalogue? updates = null,
        Action<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>? configureServer = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(url);
        if (configureServer is not null) builder.WebHost.ConfigureKestrel(configureServer);
        var app = builder.Build();
        updates ??= SignedUpdateCatalogue.Empty;
        var landing = new DownloadPage(catalogue, updates);
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok", artifacts = catalogue.Files.Count }));
        app.MapMethods("/", ["GET", "HEAD"], (HttpContext context) =>
        {
            // Older machine callers did not request HTML; preserve their contract.
            context.Response.Headers.Vary = "Accept, User-Agent, Sec-CH-UA-Platform, Sec-CH-UA-Arch";
            context.Response.Headers.CacheControl = "no-cache";
            return context.Request.GetTypedHeaders().Accept?.Any(x => x.MediaType == "text/html" && (x.Quality ?? 1) > 0) == true
                ? Results.Content(landing.Render(context.Request), "text/html; charset=utf-8")
                : Results.Json(catalogue.Manifest, DownloadCatalogue.JsonOptions);
        });
        app.MapMethods("/site/{name}", ["GET", "HEAD"], (string name) => DownloadPage.Asset(name));
        app.MapGet("/downloads.json", () => Results.Json(catalogue.Manifest, DownloadCatalogue.JsonOptions));
        app.MapMethods("/updates/{channel}.json", ["GET", "HEAD"], (string channel, HttpContext context) =>
        {
            if (!updates.TryGet(channel, out var feed)) return Results.NotFound();
            return Feed(feed!, context);
        });
        app.MapMethods("/platforms/{platform}/updates/{channel}.json", ["GET", "HEAD"],
            (string platform, string channel, HttpContext context) =>
        {
            if (!updates.TryGet(platform, channel, out var feed)) return Results.NotFound();
            return Feed(feed!, context);
        });
        app.MapMethods("/artifacts/{fileName}", ["GET", "HEAD"], (string fileName) => Artifact(fileName));
        app.MapMethods("/platforms/{platform}/artifacts/{fileName}", ["GET", "HEAD"], (string platform, string fileName) =>
        {
            if (!PlatformUpdateFeeds.IsNativePlatform(platform) || !catalogue.Files.TryGetValue(fileName, out var file)
                || file.Artifact.Platform != platform) return Results.NotFound();
            return Artifact(fileName);
        });
        return app;

        IResult Feed(SignedUpdateFeed feed, HttpContext context)
        {
            foreach (var artifact in feed!.Manifest.Release.Artifacts)
                if (!catalogue.TryGetUnchanged(artifact.FileName, out _))
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            context.Response.Headers.CacheControl = "no-cache";
            return Results.Bytes(feed.CopyEnvelope(), "application/json",
                entityTag: new EntityTagHeaderValue("\"" + feed.Sha256 + "\""));
        }
        IResult Artifact(string fileName)
        {
            if (!catalogue.Files.ContainsKey(fileName)) return Results.NotFound();
            if (!catalogue.TryGetUnchanged(fileName, out var file))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            return Results.File(file!.Path, "application/octet-stream", file.Artifact.FileName,
                lastModified: new DateTimeOffset(file.LastWriteUtc),
                entityTag: new EntityTagHeaderValue("\"" + file.Artifact.Sha256 + "\""),
                enableRangeProcessing: true);
        }
    }
}
