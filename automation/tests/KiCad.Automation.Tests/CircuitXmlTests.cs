using System.Globalization;
using KiCad.Automation.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class CircuitXmlTests
{
    [TestMethod]
    public void RepeatedSheetsAndMultiUnitSymbolsRoundTripWithoutChurn()
    {
        Circuit circuit = Fixture();
        string xml = CircuitXml.Write(circuit);
        Assert.AreEqual(xml, CircuitXml.Write(CircuitXml.Read(xml)));
        Circuit read = CircuitXml.Read(xml);
        Assert.AreEqual(2, read.Components.Count);
        Assert.AreEqual(4, read.Symbols.Count);
        Assert.AreEqual(read.Components[0].DefinitionId, read.Components[1].DefinitionId);
        Assert.AreNotEqual(read.Components[0].Id, read.Components[1].Id);
        Assert.AreNotEqual(read.Components[0].SheetInstanceId, read.Components[1].SheetInstanceId);
    }

    [TestMethod]
    public void CoordinatesAreOptionalAndDoNotDetermineConnectivity()
    {
        Circuit original = Fixture();
        Circuit read = CircuitXml.Read(CircuitXml.Write(original.WithoutPlacement()));
        Assert.IsTrue(read.Symbols.All(s => s.Placement is null));
        CollectionAssert.AreEquivalent(original.Nets.Single().Pins.ToArray(), read.Nets.Single().Pins.ToArray());
        Assert.AreEqual(CircuitXml.Write(original.WithoutPlacement()), CircuitXml.Write(read));
    }

    [TestMethod]
    public void EquivalentEnumerationAndCultureDoNotChangeXml()
    {
        Circuit circuit = Fixture();
        string expected = CircuitXml.Write(circuit);
        CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.AreEqual(expected, CircuitXml.Write(circuit with
            {
                Components = circuit.Components.Reverse().ToArray(),
                Symbols = circuit.Symbols.Reverse().ToArray(),
                SheetInstances = circuit.SheetInstances.Reverse().ToArray()
            }));
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    [TestMethod]
    public void UnknownFieldsFutureVersionsAndDtdAreRejected()
    {
        string xml = CircuitXml.Write(Fixture());
        foreach (string invalid in new[]
        {
            xml.Replace("version=\"1\"", "version=\"2\"", StringComparison.Ordinal),
            xml.Replace("<parts>", "<parts future=\"lost-data\">", StringComparison.Ordinal),
            xml.Replace("</parts>", "<future-feature /></parts>", StringComparison.Ordinal),
            "<!DOCTYPE circuit [<!ENTITY x SYSTEM 'file:///not-an-asset'>]>" + xml
        })
            Assert.AreEqual("invalid_circuit", Assert.ThrowsExactly<AutomationException>(() => CircuitXml.Read(invalid)).Code);
    }

    [TestMethod]
    public void UnknownPinsAndDoubleConnectedPinsFailBeforeSerialization()
    {
        Circuit circuit = Fixture();
        CircuitNet net = circuit.Nets.Single();
        Reject(circuit with { Nets = [net with { Pins = [new(circuit.Components[0].Id, "missing")] }] });
        Reject(circuit with { Nets = [net, new(Guid.NewGuid(), "other", [net.Pins[0]])] });
        Reject(circuit with { Nets = [net with { Pins = [net.Pins[0], net.Pins[0]] }] });
    }

    [TestMethod]
    public void BrokenHierarchyAndMissingInstancesFail()
    {
        Circuit circuit = Fixture();
        SheetInstance a = circuit.SheetInstances[0], b = circuit.SheetInstances[1];
        Reject(circuit with { SheetInstances = [a with { ParentId = b.Id }, b with { ParentId = a.Id }] });
        Reject(circuit with { SheetInstances = [a with { ParentId = Guid.NewGuid() }, b] });
        Reject(circuit with { Components = [circuit.Components[0]] });
        Reject(circuit with { Components = [circuit.Components[0], circuit.Components[1] with { DefinitionId = Guid.NewGuid() }] });
    }

    [TestMethod]
    public void ReannotationPreservesIdsAndDuplicateUnitsFail()
    {
        Circuit circuit = Fixture();
        Circuit renamed = circuit with { Components = circuit.Components.Select((c, i) => c with { Reference = "U" + (i + 20) }).ToArray() };
        Circuit roundTrip = CircuitXml.Read(CircuitXml.Write(renamed));
        CollectionAssert.AreEquivalent(circuit.Nets[0].Pins.ToArray(), roundTrip.Nets[0].Pins.ToArray());
        Reject(circuit with { Symbols = [.. circuit.Symbols, circuit.Symbols[0] with { Id = Guid.NewGuid() }] });
        Reject(circuit with { Id = circuit.Parts[0].Id });
        Reject(circuit with { Symbols = [circuit.Symbols[0] with { Placement = new(0.000001m, 0, 0, false, false, false) }] });
    }

    private static void Reject(Circuit circuit) => Assert.ThrowsExactly<AutomationException>(() => CircuitXml.Write(circuit));

    internal static Circuit Fixture()
    {
        Guid part = Guid.NewGuid(), sheet = Guid.NewGuid(), definition = Guid.NewGuid();
        Guid left = Guid.NewGuid(), right = Guid.NewGuid(), first = Guid.NewGuid(), second = Guid.NewGuid();
        return new(Guid.NewGuid(),
            [new(part, "Dual amplifier", 2, [new("1", "OUT_A", 1), new("7", "OUT_B", 2), new("8", "VCC", 0)])],
            [new(sheet, "Channel", [new(definition, part, "Example & test")])],
            [new(left, sheet, null), new(right, sheet, null)],
            [new(first, definition, left, "U1"), new(second, definition, right, "U2")],
            [new(Guid.NewGuid(), "VCC", [new(first, "8"), new(second, "8")])],
            [new(Guid.NewGuid(), first, 1, new(12.7m, 25.4m, 90, false, true, true)),
             new(Guid.NewGuid(), first, 2, null), new(Guid.NewGuid(), second, 1, null), new(Guid.NewGuid(), second, 2, null)]);
    }
}
