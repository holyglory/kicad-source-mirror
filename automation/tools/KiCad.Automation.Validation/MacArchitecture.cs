using System.Runtime.InteropServices;

namespace KiCad.Automation.Validation;

public sealed record NativeBinaryEvidence(string Name, string Architectures, string Sha256);

public static class MacArchitecture
{
    public static string CmakeName(string target) => target switch
    {
        "arm64" => "arm64",
        "x64" => "x86_64",
        _ => throw new ArgumentException("Choose Mac architecture arm64 or x64 explicitly.")
    };

    public static void RequireExecutionTarget(string target, Architecture processArchitecture)
    {
        _ = CmakeName(target);
        Architecture required = target == "arm64" ? Architecture.Arm64 : Architecture.X64;
        if (processArchitecture != required)
            throw new InvalidDataException("Run validation with a matching " + target +
                " Mac/.NET runtime. Cross-compilation alone cannot prove execution of that target.");
    }

    public static void RequireBinaryTarget(string target, string architectures)
    {
        string required = CmakeName(target);
        string[] slices = (architectures ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (slices.Length == 0 || slices.Any(s => s is not ("arm64" or "x86_64"))
            || slices.Distinct().Count() != slices.Length || !slices.Contains(required))
            throw new InvalidDataException("The native binary does not contain the requested Mac architecture.");
    }
}
