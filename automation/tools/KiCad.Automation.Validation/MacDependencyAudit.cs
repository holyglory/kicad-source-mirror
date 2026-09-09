using System.Text;
using System.Text.Json;

namespace KiCad.Automation.Validation;

public sealed record MacDependencyGroup(string Name, IReadOnlyList<string> Roots, IReadOnlyList<string> Resolved);
public sealed record MacDependencyReport(int SchemaVersion, string NativeBundle, string ManagedRoot,
    IReadOnlyList<MacDependencyGroup> Groups);

/// <summary>Generates a native Mac dependency audit, not a Linux simulation of
/// dyld. Inputs use the pinned bundle's executable/shared/module ownership.</summary>
public static class MacDependencyAudit
{
    public static string CreateScript(string bundle, string managed, string reportPath)
    {
        RequirePath(bundle); RequirePath(managed); RequirePath(reportPath);
        if (!Directory.Exists(bundle) || !Directory.Exists(managed))
            throw new DirectoryNotFoundException("Both the native app and managed runtime must exist before dependency audit.");
        string nativeMain = Path.Combine(bundle, "Contents/MacOS/kicad");
        string managedMain = Path.Combine(managed, "kicad-mcp");
        var nativeExecutables = new List<string>();
        foreach (string name in new[] { "kicad", "kicad-cli", "dxf2idf", "idf2vrml", "idfcyl", "idfrect" })
            nativeExecutables.Add(RequireFile(Path.Combine(bundle, "Contents/MacOS", name)));
        foreach (string name in new[] { "eeschema", "pcbnew", "gerbview", "bitmap2component", "pcb_calculator", "pl_editor" })
            nativeExecutables.Add(RequireFile(Path.Combine(bundle, "Contents/Applications", name + ".app", "Contents/MacOS", name)));
        string crashpad = Path.Combine(bundle, "Contents/MacOS/crashpad_handler");
        if (File.Exists(crashpad)) nativeExecutables.Add(RequireFile(crashpad));
        RequireFile(managedMain);
        string nng = MacManagedRuntime.FindBundledNng(bundle);
        var script = new StringBuilder("cmake_minimum_required(VERSION 3.21)\n");
        script.AppendLine("if(NOT CMAKE_HOST_SYSTEM_NAME STREQUAL \"Darwin\")\n  message(FATAL_ERROR \"This dependency audit must execute on macOS.\")\nendif()");
        script.AppendLine("set(native_bundle " + MacValidation.CmakeLiteral(bundle) + ")");
        script.AppendLine("set(managed_root " + MacValidation.CmakeLiteral(managed) + ")");
        script.AppendLine("set(report_path " + MacValidation.CmakeLiteral(reportPath) + ")");
        script.AppendLine("if(EXISTS \"${report_path}\")\n  message(FATAL_ERROR \"Dependency report already exists.\")\nendif()");
        script.AppendLine(Helpers);
        AppendGroup("native", nativeMain, nativeExecutables, Libraries(bundle), Modules(bundle));
        AppendGroup("managed", managedMain, [managedMain], Libraries(managed).Append(nng).Distinct(StringComparer.Ordinal), Modules(managed));
        script.AppendLine("string(JSON report SET \"${report}\" groups 0 \"${native_report}\")");
        script.AppendLine("string(JSON report SET \"${report}\" groups 1 \"${managed_report}\")");
        script.AppendLine("file(WRITE \"${report_path}\" \"${report}\")");
        return script.ToString();

        void AppendGroup(string name, string main, IEnumerable<string> executables, IEnumerable<string> libraries, IEnumerable<string> modules)
        {
            AddList(name + "_executables", executables);
            AddList(name + "_libraries", libraries);
            AddList(name + "_modules", modules);
            script.AppendLine($"audit_group({name} {MacValidation.CmakeLiteral(main)} {name}_report)");
        }
        void AddList(string name, IEnumerable<string> paths)
        {
            script.Append("set(").Append(name);
            foreach (string path in paths.Order(StringComparer.Ordinal)) script.Append('\n').Append("  ").Append(MacValidation.CmakeLiteral(RequireFile(path)));
            script.AppendLine(")");
        }
    }

    public static MacDependencyReport ReadReport(string json, string bundle, string managed)
    {
        var report = JsonSerializer.Deserialize<MacDependencyReport>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("The native dependency report is missing.");
        if (report.SchemaVersion != 1 || report.NativeBundle != bundle || report.ManagedRoot != managed || report.Groups is null
            || report.Groups.Any(g => g is null) || !report.Groups.Select(g => g.Name).Order().SequenceEqual(new[] { "managed", "native" }))
            throw new InvalidDataException("The dependency report does not match this native/managed candidate.");
        foreach (var group in report.Groups)
        {
            if (group.Roots is null || group.Roots.Count == 0 || group.Resolved is null)
                throw new InvalidDataException("A dependency group has no audited roots or result inventory.");
            foreach (string path in group.Roots.Concat(group.Resolved))
                if (!IsContained(bundle, path) && !IsContained(managed, path))
                    throw new InvalidDataException("A non-system dependency is outside the candidate: " + path);
        }
        return report;
    }

    private static IEnumerable<string> Files(string root) => Directory.EnumerateFiles(root, "*", new EnumerationOptions
    { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false });
    private static IEnumerable<string> Libraries(string root) => Files(root).Where(p => Path.GetExtension(p) == ".dylib");
    private static IEnumerable<string> Modules(string root) => Files(root).Where(p => Path.GetExtension(p) is ".kiface" or ".so");
    private static string RequireFile(string path)
    {
        RequirePath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Required dependency-audit input is missing.", path);
        return path;
    }
    private static void RequirePath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Any(c => c is '\0' or '\r' or '\n' or ';'))
            throw new ArgumentException("Dependency audit paths must be absolute and represent one CMake list item.");
    }
    private static bool IsContained(string root, string path)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path)) return false;
        string relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith("../", StringComparison.Ordinal) && !Path.IsPathFullyQualified(relative);
    }

    private const string Helpers = """
        function(json_string input output)
          string(REPLACE "\\" "\\\\" value "${input}")
          string(REPLACE "\"" "\\\"" value "${value}")
          set(${output} "\"${value}\"" PARENT_SCOPE)
        endfunction()
        json_string("${native_bundle}" native_json)
        json_string("${managed_root}" managed_json)
        set(report "{\"schemaVersion\":1,\"groups\":[]}")
        string(JSON report SET "${report}" nativeBundle "${native_json}")
        string(JSON report SET "${report}" managedRoot "${managed_json}")
        file(REAL_PATH "${native_bundle}" native_real)
        file(REAL_PATH "${managed_root}" managed_real)
        function(audit_group group main output)
          file(GET_RUNTIME_DEPENDENCIES
            EXECUTABLES ${${group}_executables}
            LIBRARIES ${${group}_libraries}
            MODULES ${${group}_modules}
            BUNDLE_EXECUTABLE "${main}"
            RESOLVED_DEPENDENCIES_VAR resolved
            UNRESOLVED_DEPENDENCIES_VAR unresolved
            CONFLICTING_DEPENDENCIES_PREFIX conflict
            PRE_EXCLUDE_REGEXES "^/usr/lib/" "^/System/Library/")
          if(unresolved OR conflict_FILENAMES)
            message(FATAL_ERROR "${group}: unresolved=${unresolved}; conflicting=${conflict_FILENAMES}")
          endif()
          set(result "{\"roots\":[],\"resolved\":[]}")
          json_string("${group}" group_json)
          string(JSON result SET "${result}" name "${group_json}")
          set(index 0)
          foreach(root IN LISTS ${group}_executables ${group}_libraries ${group}_modules)
            json_string("${root}" root_json)
            string(JSON result SET "${result}" roots ${index} "${root_json}")
            math(EXPR index "${index}+1")
          endforeach()
          set(index 0)
          foreach(dependency IN LISTS resolved)
            file(REAL_PATH "${dependency}" actual)
            string(FIND "${actual}" "${native_real}/" native_prefix)
            string(FIND "${actual}" "${managed_real}/" managed_prefix)
            if(NOT native_prefix EQUAL 0 AND NOT managed_prefix EQUAL 0)
              message(FATAL_ERROR "${group}: unbundled dependency ${actual}")
            endif()
            # Validate physical ownership, but report the equivalent path under
            # the caller's declared root (e.g. /tmp versus /private/tmp on Mac).
            if(native_prefix EQUAL 0)
              file(RELATIVE_PATH relative "${native_real}" "${actual}")
              set(reported "${native_bundle}/${relative}")
            else()
              file(RELATIVE_PATH relative "${managed_real}" "${actual}")
              set(reported "${managed_root}/${relative}")
            endif()
            json_string("${reported}" actual_json)
            string(JSON result SET "${result}" resolved ${index} "${actual_json}")
            math(EXPR index "${index}+1")
          endforeach()
          set(${output} "${result}" PARENT_SCOPE)
        endfunction()
        """;
}
