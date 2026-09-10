using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace KiCad.Automation.Mcp;

public sealed record WindowsProcessIdentity(int ProcessId, string CreationFileTime, string Executable)
{
    public static WindowsProcessIdentity Read(int processId)
    {
        using var process = WindowsObservedProcess.Open(processId);
        return process.Identity;
    }

    internal void Validate()
    {
        if (ProcessId <= 0 || !ulong.TryParse(CreationFileTime, NumberStyles.None, CultureInfo.InvariantCulture, out ulong creation)
            || creation == 0 || creation.ToString(CultureInfo.InvariantCulture) != CreationFileTime
            || string.IsNullOrEmpty(Executable) || !Path.IsPathFullyQualified(Executable))
            throw new InvalidDataException("Invalid Windows kernel process identity.");
    }
}

/// <summary>Observe one pinned kernel process object with query/synchronize
/// access only. Cancellation and disposal never signal or terminate the editor.</summary>
public sealed class WindowsObservedProcess : IDisposable
{
    private readonly SafeProcessHandle handle;
    public WindowsProcessIdentity Identity { get; }

    private WindowsObservedProcess(SafeProcessHandle handle, WindowsProcessIdentity identity)
    { this.handle = handle; Identity = identity; }

    public static WindowsObservedProcess Open(int processId)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows process identity requires Windows.");
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        var handle = OpenProcess(0x00101000, false, processId); // SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION
        if (handle.IsInvalid) { int error = Marshal.GetLastPInvokeError(); handle.Dispose(); throw Error("Cannot observe the requested Windows process.", error); }
        try
        {
            RequireAlive(handle);
            return new(handle, ReadIdentity(handle, processId));
        }
        catch { handle.Dispose(); throw; }
    }

    public static WindowsObservedProcess Open(WindowsProcessIdentity expected)
    {
        expected.Validate();
        var process = Open(expected.ProcessId);
        if (process.Identity.CreationFileTime != expected.CreationFileTime
            || !string.Equals(process.Identity.Executable, expected.Executable, StringComparison.OrdinalIgnoreCase))
        { process.Dispose(); throw new InvalidDataException("The Windows process no longer matches the requested creation time and executable."); }
        return process;
    }

    public bool IsAlive => Alive(handle);

    internal static string Observe(WindowsProcessIdentity expected, string executable)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows process observation requires Windows.");
        expected.Validate();
        using var observed = OpenProcess(0x00101000, false, expected.ProcessId);
        // Invalid parameter from this particular OpenProcess call means the
        // supplied nonzero PID no longer exists. Access denial is unknown, not exit.
        if (observed.IsInvalid) return Marshal.GetLastPInvokeError() == 87 ? "exited" : "unknown";
        try
        {
            if (!Alive(observed)) return "exited";
            var actual = ReadIdentity(observed, expected.ProcessId);
            if (actual.CreationFileTime != expected.CreationFileTime) return "identity_changed";
            if (!string.Equals(actual.Executable, expected.Executable, StringComparison.OrdinalIgnoreCase)) return "executable_mismatch";
            if (!string.Equals(Path.GetFullPath(executable), Path.GetFullPath(expected.Executable), StringComparison.OrdinalIgnoreCase))
                return "executable_mismatch";
            return Alive(observed) ? "live" : "exited";
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException)
        {
            try { return Alive(observed) ? "unknown" : "exited"; }
            catch (IOException) { return "unknown"; }
        }
    }

    private static WindowsProcessIdentity ReadIdentity(SafeProcessHandle handle, int processId)
    {
        if (!GetProcessTimes(handle, out var created, out _, out _, out _)) throw Error("Cannot read Windows process creation time.");
        var path = new StringBuilder(32768); uint capacity = (uint)path.Capacity;
        if (!QueryFullProcessImageNameW(handle, 0, path, ref capacity) || capacity == 0)
            throw Error("Cannot read Windows executable identity.");
        RequireAlive(handle);
        // Preserve unrounded FILETIME across JSON consumers with 53-bit numbers.
        var identity = new WindowsProcessIdentity(processId, (((ulong)created.High << 32) | created.Low)
            .ToString(CultureInfo.InvariantCulture), path.ToString());
        identity.Validate(); return identity;
    }

    public async Task WaitForExitAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Duplicate the kernel object handle so this wait remains valid even if
        // its parent observation is disposed. Never look the PID up again.
        if (!DuplicateHandle(GetCurrentProcess(), handle, GetCurrentProcess(), out SafeWaitHandle duplicate, 0, false, 2))
            throw Error("Cannot retain the Windows process wait handle.");
        using var wait = new ProcessWaitHandle(duplicate);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ThreadPool.RegisterWaitForSingleObject(wait, (_, _) => completed.TrySetResult(), null,
            Timeout.InfiniteTimeSpan, executeOnlyOnce: true);
        using var cancellation = token.Register(() => completed.TrySetCanceled(token));
        try { await completed.Task; }
        finally { registration.Unregister(null); }
    }

    public void Dispose() => handle.Dispose();

    private static void RequireAlive(SafeProcessHandle handle)
    { if (!Alive(handle)) throw new InvalidDataException("The requested Windows process has exited."); }
    private static bool Alive(SafeProcessHandle handle) => WaitForSingleObject(handle, 0) switch
    { 0x102 => true, 0 => false, _ => throw Error("Cannot inspect the Windows process lifetime.") };
    private static IOException Error(string message, int? code = null) => new(message, new Win32Exception(code ?? Marshal.GetLastPInvokeError()));
    private sealed class ProcessWaitHandle : WaitHandle
    { public ProcessWaitHandle(SafeWaitHandle handle) { SafeWaitHandle = handle; } }
    [StructLayout(LayoutKind.Sequential)] private struct FILETIME { public uint Low, High; }
    [DllImport("kernel32", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint rights, bool inherit, int pid);
    [DllImport("kernel32", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle process,
        out FILETIME created, out FILETIME exited, out FILETIME kernel, out FILETIME user);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageNameW(
        SafeProcessHandle process, uint flags, StringBuilder name, ref uint capacity);
    [DllImport("kernel32", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32", SetLastError = true)] private static extern bool DuplicateHandle(nint sourceProcess, SafeProcessHandle source,
        nint targetProcess, out SafeWaitHandle duplicate, uint access, bool inherit, uint options);
}
