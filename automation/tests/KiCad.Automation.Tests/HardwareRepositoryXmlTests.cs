using KiCad.Automation.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class HardwareRepositoryXmlTests
{
    private static HardwareRepository Fixture()
    {
        var designs = Enumerable.Range(1, 3).Select(n => new HardwareDesign(Guid.NewGuid(), "Board " + n,
            $"boards/{n}/board.kicad_pro", $"boards/{n}/design.xml",
            [new(Guid.NewGuid(), "Supply", "  Shared supply <rail> & return.\nKeep connector accessible.  ")])).ToArray();
        return new(Guid.NewGuid(), "Controller system", designs,
            [new(Guid.NewGuid(), "requirements/user.md", "git:fixture-revision", "User requirements\n  Preserve exact wording."),
             new(Guid.NewGuid(), "datasheets/regulator.pdf", "Rev. 3", "Operating limits, not absolute maxima")],
            [new(Guid.NewGuid(), "libraries/power.kicad_sym", "git:library-revision")],
            [new(Guid.NewGuid(), "Power", "Three-board supply", designs.Select(d => new HardwareEndpoint(d.Id, d.Ports[0].Id)).ToArray())]);
    }

    [TestMethod]
    public void MultiboardProseAndRevisionsRoundTripDeterministically()
    {
        var original = Fixture();
        string xml = HardwareRepositoryXml.Write(original);
        var read = HardwareRepositoryXml.Read(xml);
        Assert.AreEqual(xml, HardwareRepositoryXml.Write(read));
        Assert.AreEqual(original.Designs[0].Ports[0].Description, read.Designs.Single(d => d.Id == original.Designs[0].Id).Ports[0].Description);
        foreach (var source in original.Documents)
            Assert.AreEqual(source, read.Documents.Single(d => d.Id == source.Id));
        Assert.AreEqual(xml, HardwareRepositoryXml.Write(original with
        {
            Designs = original.Designs.Reverse().ToArray(), Documents = original.Documents.Reverse().ToArray(),
            Interfaces = [original.Interfaces[0] with { Endpoints = original.Interfaces[0].Endpoints.Reverse().ToArray() }]
        }));
    }

    [TestMethod]
    public void RequirementsStageMayHaveNoBoardsYet()
    {
        var repository = Fixture() with { Designs = [], Interfaces = [] };
        Assert.AreEqual(0, HardwareRepositoryXml.Read(HardwareRepositoryXml.Write(repository)).Designs.Count);
    }

    [TestMethod]
    public void UnknownOrMalformedXmlIsNotSilentlySimplified()
    {
        string xml = HardwareRepositoryXml.Write(Fixture());
        foreach (string invalid in new[]
        {
            xml.Replace("version=\"1\"", "version=\"2\"", StringComparison.Ordinal),
            xml.Replace(HardwareRepositoryXml.Namespace, "urn:kicad:automation:hardware:2", StringComparison.Ordinal),
            xml.Replace("<designs>", "<designs future=\"lost\">", StringComparison.Ordinal),
            xml.Replace("</designs>", "<future /></designs>", StringComparison.Ordinal),
            xml.Replace("id=\"", "id=\"invalid-", StringComparison.Ordinal),
            "<!DOCTYPE hardware [<!ENTITY x SYSTEM 'file:///not-a-source'>]>" + xml
        })
            Assert.AreEqual("invalid_hardware", Assert.ThrowsExactly<AutomationException>(() => HardwareRepositoryXml.Read(invalid)).Code);
    }

    [TestMethod]
    public void ReferencesAndOwnershipMustBeExact()
    {
        var r = Fixture();
        var connection = r.Interfaces[0];
        Reject(r with { Id = Guid.Empty });
        Reject(r with { Libraries = [r.Libraries[0] with { Id = r.Id }] });
        Reject(r with { Interfaces = [connection with { Endpoints = [new(Guid.NewGuid(), connection.Endpoints[0].PortId), connection.Endpoints[1]] }] });
        Reject(r with { Interfaces = [connection with { Endpoints = [new(r.Designs[0].Id, r.Designs[1].Ports[0].Id), connection.Endpoints[1]] }] });
        Reject(r with { Interfaces = [connection with { Endpoints = [connection.Endpoints[0], connection.Endpoints[0]] }] });
        Reject(r with { Interfaces = [connection, connection with { Id = Guid.NewGuid() }] });
        Reject(r with { Designs = [r.Designs[0], r.Designs[1] with { ModelPath = r.Designs[0].ProjectPath }, r.Designs[2]] });
        Reject(r with { Documents = [r.Documents[0] with { Revision = " " }] });
    }

    [TestMethod]
    public void PortablePathsRejectAmbiguousSpellings()
    {
        var r = Fixture();
        foreach (string path in new[] { "", "/board.xml", "../board.xml", "a/../board.xml", "./board.xml", "a//b.xml", "C:/board.xml", "a\\b.xml", "a/", "a\nb.xml" })
            Reject(r with { Designs = [r.Designs[0] with { ModelPath = path }, r.Designs[1], r.Designs[2]] });
    }

    private static void Reject(HardwareRepository repository) =>
        Assert.AreEqual("invalid_hardware", Assert.ThrowsExactly<AutomationException>(() => HardwareRepositoryXml.Write(repository)).Code);
}
