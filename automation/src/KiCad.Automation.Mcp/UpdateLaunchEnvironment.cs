using System.Collections.ObjectModel;

namespace KiCad.Automation.Mcp;

internal static class UpdateLaunchEnvironment
{
    private static readonly string[] Names = ["DISPLAY", "WAYLAND_DISPLAY", "XDG_RUNTIME_DIR", "KICAD_CONFIG_HOME",
        "KICAD_CACHE_HOME", "XDG_CONFIG_HOME", "XDG_CACHE_HOME", "SSL_CERT_FILE", "SSL_CERT_DIR"];

    public static IReadOnlyDictionary<string, string?> Capture(IReadOnlyDictionary<string, string?>? source)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (string name in Names)
            result[name] = source is not null && source.TryGetValue(name, out var value)
                ? value : Environment.GetEnvironmentVariable(name);
        Validate(result);
        return new ReadOnlyDictionary<string, string?>(result);
    }

    public static void Validate(IReadOnlyDictionary<string, string?>? values)
    {
        if (values is null || values.Count != Names.Length || Names.Any(name => !values.ContainsKey(name))
            || values.Any(pair => pair.Value is { } value && (value.Length > 8192 || value.Contains('\0'))))
            throw new InvalidDataException("The saved display/profile context is incomplete or unsupported.");
    }
}
