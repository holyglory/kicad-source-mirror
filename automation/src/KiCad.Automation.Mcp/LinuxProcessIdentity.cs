using System.Globalization;

namespace KiCad.Automation.Mcp;

public sealed record LinuxProcessIdentity(int ProcessId, Guid BootId, ulong StartTicks)
{
    public static LinuxProcessIdentity Read(int processId)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Kernel process identity requires Linux.");
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        Guid boot = Guid.Parse(File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim());
        return new(processId, boot, ParseStartTicks(File.ReadAllText($"/proc/{processId}/stat")));
    }

    internal static ulong ParseStartTicks(string stat)
    {
        // comm (field 2) can contain spaces and parentheses; the final closing
        // parenthesis precedes state (field 3). starttime is field 22, in kernel
        // ticks since boot, not a separately rounded wall-clock timestamp.
        int end = stat.LastIndexOf(')');
        if (end < 0 || end + 2 >= stat.Length) throw new InvalidDataException("Invalid kernel process identity.");
        string[] fields = stat[(end + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 20 || !ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out ulong ticks))
            throw new InvalidDataException("Missing kernel process start counter.");
        return ticks;
    }
}
