using KiCad.Automation.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class ComponentGuidanceTests
{
    [TestMethod]
    public void ClassAndInstanceGuidanceRetainOwnerAndSources()
    {
        var (library, binding) = Fixture();
        GuidanceResolution result = ComponentGuidance.Resolve(library, binding);
        Assert.AreEqual(3, result.Effective.Count);
        Assert.AreEqual(0, result.Conflicts.Count);
        Assert.AreEqual(1, result.Effective.Count(r => r.IsInstance));
        ResolvedGuidance inherited = result.Effective.Single(r => r.Statement.Key == "decoupling");
        Assert.AreEqual(library.Classes[1].Id, inherited.OwnerId);
        Assert.AreEqual("datasheet", inherited.Statement.Sources[0].DocumentId);
        Assert.AreEqual("Close to supply pins; exact distance is not specified.", inherited.Statement.Text);
        Assert.AreEqual(VerificationState.Unverified, inherited.Statement.Verification);
    }

    [TestMethod]
    public void ExplicitOverrideRetainsHistoryAndDoesNotDependOnEnumeration()
    {
        var (library, binding) = Fixture();
        GuidanceStatement original = library.Classes[0].Guidance[0];
        var replacement = Note("usage", "Instance-specific application") with { Replaces = original.Id };
        binding = binding with { Guidance = [.. binding.Guidance, replacement] };
        GuidanceResolution result = ComponentGuidance.Resolve(library, binding);
        Assert.AreEqual(original.Id, result.Replacements.Single().Original.Statement.Id);
        Assert.IsFalse(result.Effective.Any(r => r.Statement.Id == original.Id));
        Assert.AreEqual(replacement.Id, result.Replacements.Single().Replacement.Statement.Id);
        GuidanceResolution reversed = ComponentGuidance.Resolve(library with { Classes = library.Classes.Reverse().ToArray() },
            binding with { Guidance = binding.Guidance.Reverse().ToArray() });
        CollectionAssert.AreEqual(result.Effective.ToArray(), reversed.Effective.ToArray());
    }

    [TestMethod]
    public void RequirementExceptionRequiresRationaleAndSourceAndRetainsOriginal()
    {
        var (library, binding) = Fixture();
        GuidanceStatement requirement = library.Classes[1].Guidance[0];
        var exception = requirement with { Id = Guid.NewGuid(), Text = "Different implementation for this assembly", Replaces = requirement.Id,
            Strength = GuidanceStrength.Preference, Sources = [] };
        binding = binding with { Guidance = [exception] };
        Assert.AreEqual("requirement_exception_required", Assert.ThrowsExactly<AutomationException>(() => ComponentGuidance.Resolve(library, binding)).Code);
        exception = exception with { ExceptionRationale = "Documented assembly constraint", Sources = [new("user-requirement", "r2", null, null, null)] };
        GuidanceResolution resolved = ComponentGuidance.Resolve(library, binding with { Guidance = [exception] });
        Assert.AreEqual(requirement.Text, resolved.Replacements.Single().Original.Statement.Text);
        Assert.AreEqual(exception.ExceptionRationale, resolved.Replacements.Single().Replacement.Statement.ExceptionRationale);
    }

    [TestMethod]
    public void ConflictingAssignmentsRemainVisibleRatherThanLastWriterWins()
    {
        var (library, binding) = Fixture();
        binding = binding with { Guidance = [Note("usage", "A competing value")] };
        GuidanceResolution result = ComponentGuidance.Resolve(library, binding);
        Assert.AreEqual(2, result.Conflicts.Single().StatementIds.Count);
        Assert.AreEqual(2, result.Effective.Count(r => r.Statement.Key == "usage"));
        // Different explicitly declared contexts are not automatically conflicts.
        binding = binding with { Guidance = [binding.Guidance[0] with { Applicability = "variant:service" }] };
        Assert.AreEqual(0, ComponentGuidance.Resolve(library, binding).Conflicts.Count);
    }

    [TestMethod]
    public void LibraryUpdatesCyclesUnknownClassesAndDuplicateIdsAreRejected()
    {
        var (library, binding) = Fixture();
        Assert.ThrowsExactly<AutomationException>(() => ComponentGuidance.Resolve(library, binding with { LibraryRevision = "new" }));
        Assert.ThrowsExactly<AutomationException>(() => ComponentGuidance.Resolve(library, binding with { ClassId = Guid.NewGuid() }));
        Assert.ThrowsExactly<AutomationException>(() => ComponentGuidance.Validate(library with
        { Classes = [library.Classes[0] with { BaseClassId = library.Classes[1].Id }, library.Classes[1]] }));
        Assert.ThrowsExactly<AutomationException>(() => ComponentGuidance.Validate(library with
        { Classes = [library.Classes[0], library.Classes[1] with { Guidance = library.Classes[0].Guidance }] }));
    }

    [TestMethod]
    public void UnknownOrSiblingOverridesAndCrossComponentBindingsAreRejected()
    {
        var (library, binding) = Fixture();
        var a = Note("local", "a");
        var b = Note("local", "b") with { Replaces = a.Id };
        Assert.ThrowsExactly<AutomationException>(() => ComponentGuidance.Resolve(library, binding with { Guidance = [a, b] }));
        Assert.ThrowsExactly<AutomationException>(() => ComponentGuidance.Resolve(library, binding with { Guidance = [b] }));
        Circuit circuit = CircuitXmlTests.Fixture();
        Assert.ThrowsExactly<AutomationException>(() => ComponentGuidance.Resolve(circuit, library, binding));
        Assert.AreEqual(3, ComponentGuidance.Resolve(circuit, library, binding with { ComponentInstanceId = circuit.Components[0].Id }).Effective.Count);
    }

    [TestMethod]
    public void LibraryAndInstanceXmlPreserveMultilineTextSourcesAndInheritance()
    {
        var (library, binding) = Fixture();
        binding = binding with { Guidance = [binding.Guidance[0] with
        {
            Text = "  Side A & cooling area\nKeep service access.\n  Do not invent a distance.  ",
            Sources = [new("user-prompt", "r1", null, null, "service"), new("drawing", "B", 3, "Thermal", null)]
        }] };
        string libraryXml = ComponentKnowledgeXml.WriteLibrary(library);
        var restoredLibrary = ComponentKnowledgeXml.ReadLibrary(libraryXml);
        Assert.AreEqual(libraryXml, ComponentKnowledgeXml.WriteLibrary(restoredLibrary));
        string bindingXml = ComponentKnowledgeXml.WriteBinding(binding, library);
        var restoredBinding = ComponentKnowledgeXml.ReadBinding(bindingXml, restoredLibrary);
        Assert.AreEqual(bindingXml, ComponentKnowledgeXml.WriteBinding(restoredBinding, restoredLibrary));
        Assert.AreEqual(binding.Guidance[0].Text, restoredBinding.Guidance[0].Text);
        CollectionAssert.AreEqual(binding.Guidance[0].Sources.ToArray(), restoredBinding.Guidance[0].Sources.ToArray());
        Assert.AreEqual(3, ComponentGuidance.Resolve(restoredLibrary, restoredBinding).Effective.Count);
    }

    [TestMethod]
    public void KnowledgeXmlRejectsUnknownFieldsAndFutureVersionsWithoutSimplification()
    {
        var (library, binding) = Fixture();
        string xml = ComponentKnowledgeXml.WriteLibrary(library);
        foreach (string invalid in new[]
        {
            xml.Replace("version=\"1\"", "version=\"2\"", StringComparison.Ordinal),
            xml.Replace("<class ", "<class unknown=\"lost\" ", StringComparison.Ordinal),
            xml.Replace("</class>", "<new-feature /></class>", StringComparison.Ordinal),
            "<!DOCTYPE component-library [<!ENTITY data SYSTEM 'file:///not-a-document'>]>" + xml
        })
            Assert.ThrowsExactly<AutomationException>(() => ComponentKnowledgeXml.ReadLibrary(invalid));
        string bindingXml = ComponentKnowledgeXml.WriteBinding(binding, library);
        Assert.ThrowsExactly<AutomationException>(() => ComponentKnowledgeXml.ReadBinding(bindingXml, library with { Revision = "r2" }));
    }

    [TestMethod]
    public void ExceptionHistorySurvivesSeparateLibraryAndDesignXml()
    {
        var (library, binding) = Fixture();
        GuidanceStatement inherited = library.Classes[1].Guidance[0];
        binding = binding with { Guidance = [inherited with
        {
            Id = Guid.NewGuid(), Replaces = inherited.Id, Text = "Assembly-specific exception",
            ExceptionRationale = "Documented mounting restriction", Sources = [new("requirement", "1", null, null, null)]
        }] };
        var restoredLibrary = ComponentKnowledgeXml.ReadLibrary(ComponentKnowledgeXml.WriteLibrary(library));
        var restoredBinding = ComponentKnowledgeXml.ReadBinding(ComponentKnowledgeXml.WriteBinding(binding, library), restoredLibrary);
        var result = ComponentGuidance.Resolve(restoredLibrary, restoredBinding);
        Assert.AreEqual(inherited.Text, result.Replacements.Single().Original.Statement.Text);
        Assert.AreEqual("Documented mounting restriction", result.Replacements.Single().Replacement.Statement.ExceptionRationale);
    }

    private static GuidanceStatement Note(string key, string text) => new(Guid.NewGuid(), key, "usage", text,
        GuidanceStrength.Preference, "", [], VerificationState.Unverified);

    internal static (ComponentKnowledgeLibrary, ComponentKnowledgeBinding) Fixture()
    {
        Guid generic = Guid.NewGuid(), specific = Guid.NewGuid(), library = Guid.NewGuid();
        return (new(library, "r1", [
            new(generic, "Processor", null, [Note("usage", "General processor guidance")]),
            new(specific, "Concrete processor", generic, [new(Guid.NewGuid(), "decoupling", "power",
                "Close to supply pins; exact distance is not specified.", GuidanceStrength.Requirement, "",
                [new("datasheet", "rev-A", 12, "Supply requirements", null)])])]),
            new(Guid.NewGuid(), library, "r1", specific, [Note("thermal", "Prefer the declared cooling area") with { Category = "placement" }]));
    }
}
