using System.Text;
using System.Text.Json;
using KiCad.Automation.Validation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class WindowsRequalifiedPreviewTests
{
    [TestMethod]
    public void ANewPassingRecordMustBindTheOriginalFailedBuildAndExactBytes()
    {
        string commit = new('a', 40), hash = new('b', 64), harness = new('c', 40);
        var original = JsonSerializer.SerializeToElement(new
        {
            SchemaVersion = 1, Status = "failed", SourceCommit = commit, Platform = "windows", Architecture = "x64", RunId = "123",
            Steps = new[] { "source-commit", "pinned-ancestry", "native-build", "native-tests", "native-install", "installed-native-commit", "managed-runtime", "managed-contracts" }
                .Select(name => new { Name = name, ExitCode = 0 }).Append(new { Name = "installed-editor-journey", ExitCode = 1 }).ToArray(),
            DiagnosticArtifacts = new[] { new { Path = "unqualified-windows-" + commit + ".zip", Bytes = 1234L, Sha256 = hash } }
        });
        var inputs = JsonSerializer.SerializeToElement(new { sourceCommit = commit, archiveSha256 = hash, archiveBytes = 1234L,
            originalRunId = "123", rebuiltNativeCode = false, harnessCommit = harness });
        var result = JsonSerializer.SerializeToElement(new { schemaVersion = 1, status = "passed", sourceCommit = commit,
            archiveSha256 = hash, rebuiltNativeCode = false, exactRetainedPayload = true, nativeEditorJourneyPassed = true, originalReceiptUnchanged = true });
        var bound = WindowsRequalifiedPreview.ValidateProvenance(original, inputs, result, commit);
        Assert.AreEqual(hash, bound.Sha256); Assert.AreEqual(harness, bound.HarnessCommit);
        foreach (var invalid in new[] { Replace(result, "passed", "failed"), Replace(result, hash, new string('d', 64)),
            Replace(result, "\"nativeEditorJourneyPassed\":true", "\"nativeEditorJourneyPassed\":false") })
            Assert.ThrowsExactly<InvalidDataException>(() => WindowsRequalifiedPreview.ValidateProvenance(original, inputs, invalid, commit));
        Assert.ThrowsExactly<InvalidDataException>(() => WindowsRequalifiedPreview.ValidateProvenance(Replace(original,
            "\"Name\":\"native-build\",\"ExitCode\":0", "\"Name\":\"native-build\",\"ExitCode\":1"), inputs, result, commit));
        Assert.ThrowsExactly<InvalidDataException>(() => WindowsRequalifiedPreview.ValidateProvenance(original,
            Replace(inputs, "\"archiveBytes\":1234", "\"archiveBytes\":1235"), result, commit));
        Assert.ThrowsExactly<InvalidDataException>(() => WindowsRequalifiedPreview.ValidateProvenance(original, inputs, result, new string('e', 40)));
    }

    [TestMethod]
    public void FunctionalResultCannotHideAFailedCleanupOrSkippedOuterTest()
    {
        string xml = """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results><UnitTestResult testName="OperateTheExactRetainedNativePackageWithoutRebuildingIt" outcome="Passed" /></Results>
              <ResultSummary><Counters total="1" executed="1" passed="1" failed="0" error="0" timeout="0" aborted="0" inconclusive="0" notExecuted="0" /></ResultSummary>
            </TestRun>
            """;
        WindowsRequalifiedPreview.RequirePassingOuterTest(Encoding.UTF8.GetBytes(xml));
        foreach (string invalid in new[] { xml.Replace("outcome=\"Passed\"", "outcome=\"Failed\""),
            xml.Replace("failed=\"0\"", "failed=\"1\""), xml.Replace("executed=\"1\"", "executed=\"0\""),
            xml.Replace("WithoutRebuildingIt", "SomeOtherTest") })
            Assert.ThrowsExactly<InvalidDataException>(() => WindowsRequalifiedPreview.RequirePassingOuterTest(Encoding.UTF8.GetBytes(invalid)));
    }

    private static JsonElement Replace(JsonElement value, string before, string after)
    {
        using var result = JsonDocument.Parse(value.GetRawText().Replace(before, after, StringComparison.Ordinal));
        return result.RootElement.Clone();
    }
}
