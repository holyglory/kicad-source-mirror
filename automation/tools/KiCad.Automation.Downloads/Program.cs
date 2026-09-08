using KiCad.Automation.Downloads;

string root = Environment.GetEnvironmentVariable("KICAD_DOWNLOAD_ROOT")
    ?? throw new InvalidOperationException("KICAD_DOWNLOAD_ROOT must name the dedicated public artifact directory.");
if (!int.TryParse(Environment.GetEnvironmentVariable("PORT"), out int port) || port is < 1 or > 65535)
    throw new InvalidOperationException("PORT must be the Coordinator-assigned local port.");
DownloadCatalogue catalogue = await DownloadCatalogue.LoadAsync(root);
// The Coordinator edge owns public HTTPS. No native IPC, control, repository,
// directory listing, upload endpoint or administration endpoint is exposed.
await using var app = DownloadServer.Create(catalogue, "http://127.0.0.1:" + port);
await app.RunAsync();
