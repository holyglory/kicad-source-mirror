using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KiCad.Automation.Tests;

// A test-owned lifetime boundary, not an execution-capacity controller. MCP
// disconnects leave this handle open; final cleanup closes only this test's job.
internal sealed class WindowsProcessJob : IDisposable
{
    private readonly SafeFileHandle handle;

    public WindowsProcessJob()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        handle = CreateJobObjectW(0, null);
        if (handle.IsInvalid) throw new Win32Exception();
        var limits = new EXTENDED_LIMIT_INFORMATION { Basic = new() { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE
        if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<EXTENDED_LIMIT_INFORMATION>()))
        { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
    }

    public void Attach(Process process)
    {
        if (!AssignProcessToJobObject(handle, process.SafeHandle)) throw new Win32Exception();
    }

    public bool Contains(Process process)
    {
        if (!IsProcessInJob(process.SafeHandle, handle, out bool belongs)) throw new Win32Exception();
        return belongs;
    }

    public void Dispose() => handle.Dispose();

    [StructLayout(LayoutKind.Sequential)] private struct BASIC_LIMIT_INFORMATION
    {
        public long PerProcessTime, PerJobTime; public uint Flags; public nuint MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcessLimit; public nuint Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IO_COUNTERS
    { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct EXTENDED_LIMIT_INFORMATION
    {
        public BASIC_LIMIT_INFORMATION Basic; public IO_COUNTERS Io;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass,
        ref EXTENDED_LIMIT_INFORMATION information, uint size);
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool IsProcessInJob(SafeProcessHandle process, SafeFileHandle job, out bool result);
}
