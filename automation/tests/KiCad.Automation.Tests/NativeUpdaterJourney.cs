using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using KiCad.Automation.Distribution;
using KiCad.Automation.Native;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    [TestMethod]
    [TestCategory("NativeUpdater")]
    public async Task RenderedManagerOwnsRealHelperAndClosesDuringPendingCheck()
    {
        string root = FindRoot();
        string evidence = NativeEvidenceDirectory.Begin(Path.Combine(root, "automation/artifacts"));
        string temporary = Directory.CreateTempSubdirectory("kicad-native-updater-").FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var tlsKey = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", tlsKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        using var publisher = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] envelope = UpdateManifestCodec.Sign(new(1, "kicad-codex", "preview", 1, "fixture", new string('1', 40),
            [new("linux-x64", "tar.gz", "fixture.tar.gz", 1, new string('2', 64))]), publisher);
        int slow = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        await using var app = builder.Build();
        app.MapGet("/updates/preview.json", async (HttpContext context) =>
        {
            if (Volatile.Read(ref slow) == 1)
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, context.RequestAborted); }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { disconnected.TrySetResult(); }
            }
            else await Results.Bytes(envelope, "application/json").ExecuteAsync(context);
        });
        var processes = new List<Process>();
        var captures = new List<Task>();
        string? activeDisplay = null;
        try
        {
            await app.StartAsync(deadline.Token);
            string origin = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single() + "/";
            string certificateFile = Path.Combine(temporary, "fixture-ca.pem");
            await File.WriteAllTextAsync(certificateFile, certificate.ExportCertificatePem(), deadline.Token);
            string receipt = Path.Combine(temporary, "installed.json");
            await File.WriteAllBytesAsync(receipt, envelope, deadline.Token);
            string state = Directory.CreateDirectory(Path.Combine(temporary, "state")).FullName;
            string configuration = Path.Combine(temporary, "updater.json");
            await File.WriteAllTextAsync(configuration, JsonSerializer.Serialize(new UpdatePreparationConfiguration(1, origin,
                Convert.ToBase64String(publisher.ExportSubjectPublicKeyInfo()), receipt, state, temporary,
                "preview", "linux-x64", "tar.gz"), new JsonSerializerOptions(JsonSerializerDefaults.Web)), deadline.Token);
            var displayStart = new ProcessStartInfo("Xvfb") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "-displayfd", "1", "-screen", "0", "1280x900x24", "-nolisten", "tcp" }) displayStart.ArgumentList.Add(arg);
            Process display = Process.Start(displayStart)!;
            processes.Add(display);
            captures.Add(Capture(display.StandardError, Path.Combine(evidence, "updater-xvfb.stderr.log")));
            string displayNumber = (await display.StandardOutput.ReadLineAsync(deadline.Token))!;
            Assert.IsTrue(int.TryParse(displayNumber, out _));
            string fixtureDisplay = ":" + displayNumber;
            activeDisplay = fixtureDisplay;
            foreach (string mode in new[] { "current", "invalid", "pending" })
            {
                Volatile.Write(ref slow, mode == "pending" ? 1 : 0);
                string projectDirectory = Directory.CreateDirectory(Path.Combine(temporary, mode)).FullName;
                string project = Path.Combine(projectDirectory, "fixture.kicad_pro");
                await File.WriteAllTextAsync(project, "{\"meta\":{\"version\":3}}", deadline.Token);
                string instance = Guid.NewGuid().ToString("D");
                string socket = Path.Combine(projectDirectory, "api.sock");
                string log = Path.Combine(evidence, "updater-" + mode + ".log");
                string usedConfiguration = configuration;
                if (mode == "invalid")
                {
                    usedConfiguration = Path.Combine(projectDirectory, "invalid.json");
                    await File.WriteAllTextAsync(usedConfiguration, "{}", deadline.Token);
                }
                var start = new ProcessStartInfo(Path.Combine(root, "automation/artifacts/native/kicad/kicad"))
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = projectDirectory };
                start.Environment["DISPLAY"] = fixtureDisplay;
                start.Environment["KICAD_RUN_FROM_BUILD_DIR"] = "1";
                start.Environment["WXTRACE"] = "KICAD_AUTOMATION_UPDATES";
                start.Environment["KICAD_CONFIG_HOME"] = Path.Combine(projectDirectory, "config");
                start.Environment["KICAD_CACHE_HOME"] = Path.Combine(projectDirectory, "cache");
                start.Environment["KICAD_AUTOMATION_UPDATE_HELPER"] = Path.Combine(root, "automation/artifacts/updater-helper/kicad-mcp");
                start.Environment["KICAD_AUTOMATION_UPDATE_CONFIG"] = usedConfiguration;
                start.Environment["SSL_CERT_FILE"] = certificateFile;
                start.Environment["SSL_CERT_DIR"] = Directory.CreateDirectory(Path.Combine(temporary, "empty-certs")).FullName;
                foreach (string arg in new[] { "--new", "--automation", instance, "--api-socket", socket, "--automation-log", log,
                    "--software-rendering", project }) start.ArgumentList.Add(arg);
                Process native = Process.Start(start)!;
                processes.Add(native);
                captures.Add(Capture(native.StandardOutput, Path.Combine(evidence, mode + ".stdout.log")));
                captures.Add(Capture(native.StandardError, Path.Combine(evidence, mode + ".stderr.log")));
                var registry = new InstanceRegistry(new NngTransport(), Path.Combine(projectDirectory, "registry"));
                while (true)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    Assert.IsFalse(native.HasExited);
                    try { await registry.AttachAsync("ipc://" + socket, instance, deadline.Token); break; }
                    catch (NngException) { }
                    catch (NativeApiException error) when (error.Status is 4 or 7) { }
                    await Task.Delay(100, deadline.Token);
                }
                Process? ownedHelper = null;
                if (mode == "pending")
                {
                    await entered.Task.WaitAsync(deadline.Token);
                    string startedLine = (await File.ReadAllLinesAsync(log, deadline.Token))
                        .Single(line => line.Contains("\"status\":\"helper_started\"", StringComparison.Ordinal));
                    using var started = JsonDocument.Parse(startedLine[startedLine.IndexOf('{')..]);
                    ownedHelper = Process.GetProcessById(started.RootElement.GetProperty("helperPid").GetInt32());
                    Assert.IsFalse(ownedHelper.HasExited);
                }
                else
                {
                    string expected = mode == "current" ? "\"status\":\"up_to_date\"" : "\"status\":\"failed\"";
                    while (!File.Exists(log) || !(await File.ReadAllTextAsync(log, deadline.Token)).Contains(expected, StringComparison.Ordinal))
                    {
                        Assert.IsFalse(native.HasExited);
                        await Task.Delay(100, deadline.Token);
                    }
                }
                await NativeKeyboard.CaptureAsync(fixtureDisplay, Path.Combine(evidence, "manager-" + mode + ".png"), deadline.Token);
                try
                {
                    NativeKeyboard.SchematicShortcut(fixtureDisplay, native.Id, "q", "KiCad", controlKey: true, focusCanvas: false);
                    await native.WaitForExitAsync(deadline.Token);
                    Assert.AreEqual(0, native.ExitCode);
                    if (ownedHelper is not null)
                    {
                        await disconnected.Task.WaitAsync(deadline.Token);
                        await ownedHelper.WaitForExitAsync(deadline.Token);
                        Assert.IsTrue(ownedHelper.HasExited, "The exact native-owned helper must finish after manager close.");
                    }
                }
                finally { ownedHelper?.Dispose(); }
            }
        }
        finally
        {
            if (activeDisplay is not null && processes.Any(process => !process.HasExited))
            {
                try { await NativeKeyboard.CaptureAsync(activeDisplay, Path.Combine(evidence, "final-display.png"), CancellationToken.None); }
                catch (Exception error) when (error is InvalidOperationException or OperationCanceledException or IOException) { }
            }
            foreach (Process process in processes.AsEnumerable().Reverse())
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                process.Dispose();
            }
            await Task.WhenAll(captures);
            await app.StopAsync();
            Directory.Delete(temporary, recursive: true);
        }
    }
}
