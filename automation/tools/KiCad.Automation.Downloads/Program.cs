using KiCad.Automation.Downloads;

string root = Environment.GetEnvironmentVariable("KICAD_DOWNLOAD_ROOT")
    ?? throw new InvalidOperationException("KICAD_DOWNLOAD_ROOT must name the dedicated public artifact directory.");
if (!int.TryParse(Environment.GetEnvironmentVariable("PORT"), out int port) || port is < 1 or > 65535)
    throw new InvalidOperationException("PORT must be the Coordinator-assigned local port.");
// Verify the complete retained catalogue before opening the replacement listener.
// The prior deployment keeps serving while this startup proof runs.
var catalogueClock = System.Diagnostics.Stopwatch.StartNew();
Console.WriteLine("Verifying public download archive hashes before startup.");
DownloadCatalogue catalogue = await DownloadCatalogue.LoadAsync(root);
Console.WriteLine($"Verified {catalogue.Files.Count} public artifacts in {catalogueClock.Elapsed.TotalSeconds:F1} seconds.");
string? publisherSpki = Environment.GetEnvironmentVariable("KICAD_UPDATE_PUBLISHER_SPKI_FILE");
SignedUpdateCatalogue updates = publisherSpki is null ? SignedUpdateCatalogue.Empty
    : await SignedUpdateCatalogue.LoadAsync(root, publisherSpki, catalogue);
// The Coordinator edge owns public HTTPS. No native IPC, control, repository,
// directory listing, upload endpoint or administration endpoint is exposed.
bool ready = false;
string origin = "http://127.0.0.1:" + port;
await using var app = DownloadServer.Create(catalogue, origin, updates, isReady: () => ready);
await app.StartAsync();
// Finish the real HTML response path before the deployment can become healthy.
// This catches missing compiled content and keeps first-request JIT off the
// visitor's path; no external request, timed delay or test-only warmup.
using (var probe = new HttpClient { BaseAddress = new Uri(origin), Timeout = TimeSpan.FromSeconds(30) })
{
    using var request = new HttpRequestMessage(HttpMethod.Get, "/");
    request.Headers.Accept.ParseAdd("text/html");
    using var response = await probe.SendAsync(request, app.Lifetime.ApplicationStopping);
    response.EnsureSuccessStatusCode();
    if (response.Content.Headers.ContentType?.MediaType != "text/html"
        || !(await response.Content.ReadAsStringAsync(app.Lifetime.ApplicationStopping)).StartsWith("<!doctype html>", StringComparison.Ordinal))
        throw new InvalidOperationException("The compiled download page did not become ready.");
}
ready = true;
await app.WaitForShutdownAsync();
