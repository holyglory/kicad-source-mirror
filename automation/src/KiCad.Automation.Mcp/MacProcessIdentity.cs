using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace KiCad.Automation.Mcp;

public sealed record MacProcessIdentity(int ProcessId, Guid BootId, ulong StartSeconds, ulong StartMicroseconds,
    string Executable)
{
    public static MacProcessIdentity Read(int processId)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Mac kernel identity requires native macOS.");
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        byte[] boot = new byte[128]; nuint length = (nuint)boot.Length;
        if (Sysctl("kern.bootsessionuuid", boot, ref length, 0, 0) != 0 || length > (nuint)boot.Length)
            throw new IOException("Mac boot-session identity is unavailable.", new Win32Exception(Marshal.GetLastPInvokeError()));
        if (!Guid.TryParse(Encoding.UTF8.GetString(boot, 0, (int)length).TrimEnd('\0'), out var bootId) || bootId == Guid.Empty)
            throw new InvalidDataException("Mac boot-session identity is invalid.");
        byte[] first = ReadBsd(processId);
        byte[] path = new byte[4096];
        int bytes = PidPath(processId, path, (uint)path.Length);
        if (bytes <= 0 || bytes >= path.Length)
            throw new IOException("Mac executable identity is unavailable.", new Win32Exception(Marshal.GetLastPInvokeError()));
        string executable = Encoding.UTF8.GetString(path, 0, bytes).TrimEnd('\0');
        if (!Path.IsPathFullyQualified(executable)) throw new InvalidDataException("Mac kernel returned a relative executable path.");
        byte[] second = ReadBsd(processId);
        var before = Decode(first, processId, bootId, executable);
        var after = Decode(second, processId, bootId, executable);
        if (before != after) throw new InvalidDataException("Mac process changed while its identity was read.");
        return after;
    }

    internal static MacProcessIdentity Decode(ReadOnlySpan<byte> bsd, int pid, Guid boot, string executable)
    {
        // proc_bsdinfo in Apple's public proc_info.h: fixed 136-byte structure
        // for the supported 64-bit targets, with start timeval at offsets120/128.
        if (bsd.Length != 136 || BinaryPrimitives.ReadUInt32LittleEndian(bsd[12..]) != pid
            || BinaryPrimitives.ReadUInt32LittleEndian(bsd[4..]) == 5) // SZOMB
            throw new InvalidDataException("Mac process snapshot is truncated, exited or belongs to another PID.");
        ulong seconds = BinaryPrimitives.ReadUInt64LittleEndian(bsd[120..]);
        ulong microseconds = BinaryPrimitives.ReadUInt64LittleEndian(bsd[128..]);
        if (seconds == 0 || microseconds >= 1_000_000 || boot == Guid.Empty || !Path.IsPathFullyQualified(executable))
            throw new InvalidDataException("Mac kernel start identity is invalid.");
        return new(pid, boot, seconds, microseconds, executable);
    }

    private static byte[] ReadBsd(int pid)
    {
        byte[] bsd = new byte[136];
        if (PidInfo(pid, 3, 0, bsd, bsd.Length) != bsd.Length)
            throw new IOException("Mac process start identity is unavailable.", new Win32Exception(Marshal.GetLastPInvokeError()));
        return bsd;
    }

    [DllImport("/usr/lib/libproc.dylib", EntryPoint = "proc_pidinfo", SetLastError = true)]
    private static extern int PidInfo(int pid, int flavor, ulong arg, [Out] byte[] buffer, int bytes);
    [DllImport("/usr/lib/libproc.dylib", EntryPoint = "proc_pidpath", SetLastError = true)]
    private static extern int PidPath(int pid, [Out] byte[] buffer, uint bytes);
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "sysctlbyname", SetLastError = true)]
    private static extern int Sysctl([MarshalAs(UnmanagedType.LPUTF8Str)] string name, [Out] byte[] value,
        ref nuint bytes, nint newValue, nuint newBytes);
}
