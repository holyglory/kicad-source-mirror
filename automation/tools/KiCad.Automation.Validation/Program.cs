using System.Text.Json;
using KiCad.Automation.Validation;

try
{
    if (args.Length == 0 || args[0] is "--help" or "-h")
    {
        Console.WriteLine("kicad-validate mac --repository LOCAL_CHECKOUT --commit FULL_SHA --architecture arm64|x64 --builder MAC_BUILDER_CHECKOUT --toolchain EXISTING_CMAKE_TOOLCHAIN --output NEW_DIRECTORY [--native-tests CTEST_REGEX]");
        Console.WriteLine("kicad-validate verify --result RESULT_JSON --archive EVIDENCE_TAR_GZ --commit FULL_SHA [--architecture arm64|x64]");
        Console.WriteLine("kicad-validate stage-linux --build NATIVE_BUILD --managed SELF_CONTAINED_PUBLISH --nng NNG_SHARED_LIBRARY --output EXISTING_STAGING_DIRECTORY");
        Console.WriteLine("kicad-validate package-linux --staging STAGING_RECEIPT --repository COMMITTED_SOURCE --commit FULL_SHA --version VERSION --output NEW_DIRECTORY");
        Console.WriteLine("kicad-validate package-debian --catalogue FROZEN_DOWNLOAD_CATALOGUE --output NEW_DIRECTORY");
        return 0;
    }
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int index = 1; index < args.Length; index += 2)
    {
        if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal)
            || !options.TryAdd(args[index][2..], args[index + 1]))
            throw new ArgumentException("Options require one value each and cannot be repeated.");
    }
    string Required(string name) => options.TryGetValue(name, out string? value) ? value
        : throw new ArgumentException($"Missing --{name}.");
    string[] allowed = args[0] == "mac"
        ? ["repository", "commit", "architecture", "builder", "toolchain", "output", "native-tests"]
        : args[0] == "stage-linux" ? ["build", "managed", "nng", "output"]
        : args[0] == "package-linux" ? ["staging", "repository", "commit", "version", "output"]
        : args[0] == "package-debian" ? ["catalogue", "output"]
        : ["result", "archive", "commit", "architecture"];
    foreach (string name in options.Keys)
        if (!allowed.Contains(name, StringComparer.Ordinal)) throw new ArgumentException($"Unknown option --{name}.");
    using var cancel = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
    if (args[0] == "package-debian")
    {
        var result = await DebianPackage.CreateAsync(Required("catalogue"), Required("output"), cancel.Token);
        Console.WriteLine(JsonSerializer.Serialize(result, Evidence.JsonOptions));
        return 0;
    }
    if (args[0] == "package-linux")
    {
        var manifest = await LinuxPackage.CreateAsync(new(Required("staging"), Required("repository"),
            Required("commit"), Required("version"), Required("output")), cancel.Token);
        Console.WriteLine(JsonSerializer.Serialize(manifest, Evidence.JsonOptions));
        return 0;
    }
    if (args[0] == "stage-linux")
    {
        var result = await LinuxStaging.RunAsync(new(Required("build"), Required("managed"),
            Required("nng"), Required("output")), cancel.Token);
        Console.WriteLine(JsonSerializer.Serialize(new { result.Status, result.Directory, result.Failure,
            FileCount = result.Files.Count, result.QualifyingDelivery }, Evidence.JsonOptions));
        return result.Status == "staged" ? 0 : 1;
    }
    if (args[0] == "mac")
    {
        var request = new MacRequest(Required("repository"), Required("commit"), Required("builder"),
            Required("toolchain"), Required("output"), options.GetValueOrDefault("native-tests", "."), Required("architecture"));
        ValidationResult result = await MacValidation.RunAsync(request, cancel.Token);
        Console.WriteLine(JsonSerializer.Serialize(result, Evidence.JsonOptions));
        return result.Status == "checks_passed" ? 0 : 1;
    }
    if (args[0] == "verify")
    {
        await Evidence.VerifyAsync(Required("result"), Required("archive"), Required("commit"), cancel.Token,
            options.GetValueOrDefault("architecture"));
        Console.WriteLine("Receipt/archive integrity verified for the requested commit. No Mac execution was performed by this command; this is not cross-platform readiness.");
        return 0;
    }
    throw new ArgumentException("Choose mac, verify, stage-linux, package-linux or package-debian.");
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
