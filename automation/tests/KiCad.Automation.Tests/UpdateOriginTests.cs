using KiCad.Automation.Mcp;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class UpdateOriginTests
{
    private static string Endpoint => NativeIpcEndpoint.FromSocketPath(Path.Combine(Path.GetTempPath(), "kicad-origin-fixture.sock"));
    private const string Project = "/fixture/test.kicad_pro";

    [TestMethod]
    public async Task VerifiesOnlyTheCapturedOriginalInstanceProjectAndEpoch()
    {
        var transport = new NativeClientTests.FixtureTransport();
        var origin = new UpdateOrigin(Endpoint, transport.Epoch);
        await UpdateOrigin.VerifyAsync(origin, Guid.Parse(transport.InstanceId), Project, default, transport);
        Assert.IsNotNull(transport.LastRequest);
        Assert.IsTrue(transport.LastRequest.Message.Is(GetAutomationSession.Descriptor));
        Assert.AreEqual(origin.Epoch, transport.LastRequest.Header.KicadToken);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => UpdateOrigin.VerifyAsync(origin, Guid.NewGuid(), Project, default, transport));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => UpdateOrigin.VerifyAsync(origin, Guid.Parse(transport.InstanceId), "/different/project.kicad_pro", default, transport));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => UpdateOrigin.VerifyAsync(origin with { Epoch = "stale" }, Guid.Parse(transport.InstanceId), Project, default, transport));
        transport.WrongType = true;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => UpdateOrigin.VerifyAsync(origin, Guid.Parse(transport.InstanceId), Project, default, transport));
    }

    [TestMethod]
    public async Task CancellationAndMalformedOriginsDoNotReachThePeer()
    {
        var transport = new NativeClientTests.FixtureTransport();
        var origin = new UpdateOrigin(Endpoint, transport.Epoch);
        await Assert.ThrowsAsync<OperationCanceledException>(() => UpdateOrigin.VerifyAsync(origin, Guid.Parse(transport.InstanceId), Project, new CancellationToken(true), transport));
        Assert.IsNull(transport.LastRequest);
        foreach (string epoch in new[] { "", " ", new string('x', 257), "bad\nepoch" })
            Assert.ThrowsExactly<InvalidDataException>(() => (origin with { Epoch = epoch }).Validate());
        Assert.Throws<ArgumentException>(() => (origin with { Endpoint = "tcp://localhost:80" }).Validate());
        Assert.IsNull(transport.LastRequest);
    }

    [TestMethod]
    public async Task LegacyRequestsDoNotAcquireImplicitReconnectAuthority()
    {
        var transport = new NativeClientTests.FixtureTransport();
        await UpdateOrigin.VerifyAsync(null, Guid.Parse(transport.InstanceId), Project, default, transport);
        Assert.IsNull(transport.LastRequest);
        var origin = new UpdateOrigin(Endpoint, transport.Epoch);
        foreach (var (old, current) in new[] { (1, 2), (2, 3) })
        {
            UpdateOrigin.ValidateRecorded(null, old, current);
            Assert.ThrowsExactly<InvalidDataException>(() => UpdateOrigin.ValidateRecorded(origin, old, current));
            UpdateOrigin.ValidateRecorded(origin, current, current);
        }
    }

    [TestMethod]
    public async Task UnavailableOrMalformedPeersCannotAuthorizeShutdown()
    {
        var transport = new NativeClientTests.FixtureTransport();
        var origin = new UpdateOrigin(Endpoint, transport.Epoch);
        foreach (int status in new[] { 2, 4, 7 })
        {
            transport.Status = status;
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => UpdateOrigin.VerifyAsync(origin, Guid.Parse(transport.InstanceId), Project, default, transport));
        }
    }
}
