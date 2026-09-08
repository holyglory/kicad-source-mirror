using Microsoft.Net.Http.Headers;

namespace KiCad.Automation.Downloads;

public static class DownloadServer
{
    public static WebApplication Create(DownloadCatalogue catalogue, string url, SignedUpdateCatalogue? updates = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(url);
        var app = builder.Build();
        updates ??= SignedUpdateCatalogue.Empty;
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok", artifacts = catalogue.Files.Count }));
        app.MapGet("/", () => Results.Json(catalogue.Manifest, DownloadCatalogue.JsonOptions));
        app.MapGet("/downloads.json", () => Results.Json(catalogue.Manifest, DownloadCatalogue.JsonOptions));
        app.MapMethods("/updates/{channel}.json", ["GET", "HEAD"], (string channel, HttpContext context) =>
        {
            if (!updates.TryGet(channel, out var feed)) return Results.NotFound();
            foreach (var artifact in feed!.Manifest.Release.Artifacts)
                if (!catalogue.TryGetUnchanged(artifact.FileName, out _))
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            context.Response.Headers.CacheControl = "no-cache";
            return Results.Bytes(feed.CopyEnvelope(), "application/json",
                entityTag: new EntityTagHeaderValue("\"" + feed.Sha256 + "\""));
        });
        app.MapMethods("/artifacts/{fileName}", ["GET", "HEAD"], (string fileName) =>
        {
            if (!catalogue.Files.ContainsKey(fileName)) return Results.NotFound();
            if (!catalogue.TryGetUnchanged(fileName, out var file))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            return Results.File(file!.Path, "application/octet-stream", file.Artifact.FileName,
                lastModified: new DateTimeOffset(file.LastWriteUtc),
                entityTag: new EntityTagHeaderValue("\"" + file.Artifact.Sha256 + "\""),
                enableRangeProcessing: true);
        });
        return app;
    }
}
