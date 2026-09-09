using System.Runtime.InteropServices;
using KiCad.Automation.Validation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class HostedDeliveryTests
{
    [TestMethod]
    public void NativeTargetsDoNotTurnLinuxOrRosettaIntoAnotherArchitecture()
    {
        HostedDelivery.ValidateTarget("arm64", true, false, Architecture.Arm64);
        HostedDelivery.ValidateTarget("x64", true, false, Architecture.X64);
        HostedDelivery.ValidateTarget("x64", false, true, Architecture.X64);
        Assert.ThrowsExactly<PlatformNotSupportedException>(() => HostedDelivery.ValidateTarget("x64", false, false, Architecture.X64));
        Assert.ThrowsExactly<PlatformNotSupportedException>(() => HostedDelivery.ValidateTarget("arm64", false, true, Architecture.Arm64));
        Assert.ThrowsExactly<PlatformNotSupportedException>(() => HostedDelivery.ValidateTarget("x64", false, true, Architecture.Arm64));
        Assert.ThrowsExactly<InvalidDataException>(() => HostedDelivery.ValidateTarget("arm64", true, false, Architecture.X64));
    }

    [TestMethod]
    public async Task InvalidSourceCannotCreateOrBootstrapOutput()
    {
        string root = Directory.CreateTempSubdirectory("kicad-hosted-contract-").FullName;
        try
        {
            string output = Path.Combine(root, "must-not-exist");
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => HostedDelivery.RunAsync(
                new(root, "master", "x64", output, new string('a', 40)), CancellationToken.None));
            Assert.IsFalse(Directory.Exists(output));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task RandomBytesAreNotWindowsExecutableEvidence()
    {
        string root = Directory.CreateTempSubdirectory("kicad-pe-contract-").FullName;
        try
        {
            string path = Path.Combine(root, "not-a-binary.exe");
            await File.WriteAllTextAsync(path, "Synthetic malformed PE fixture.");
            Assert.ThrowsExactly<BadImageFormatException>(() => HostedDelivery.RequireWindowsX64(path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
