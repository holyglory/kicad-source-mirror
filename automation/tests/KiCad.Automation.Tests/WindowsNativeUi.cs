using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace KiCad.Automation.Tests;

// QA-only, process-scoped interaction with actual native windows. No desktop
// permission changes, window-message business-handler shortcuts, or global search.
internal static class WindowsNativeUi
{
    public static async Task<nint> WaitForWindow(Process owner, string titlePart, CancellationToken token)
    {
        RequireWindows();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (owner.HasExited) throw new IOException("The owned native process exited before showing its window.");
            var matches = new List<nint>();
            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out uint pid);
                if (pid == owner.Id && IsWindowVisible(window))
                {
                    var title = new StringBuilder(1024);
                    GetWindowTextW(window, title, title.Capacity);
                    if (title.ToString().Contains(titlePart, StringComparison.Ordinal)) matches.Add(window);
                }
                return true;
            }, 0);
            if (matches.Count > 1) throw new InvalidDataException("More than one owned window matches the requested title.");
            if (matches.Count == 1) { Validate(owner, matches[0]); return matches[0]; }
            await Task.Delay(250, token);
        }
    }

    public static void Save(Process owner, nint window) => Shortcut(owner, window, 0x11, 0x53); // Ctrl+S
    public static void Close(Process owner, nint window) => Shortcut(owner, window, 0x12, 0x73); // Alt+F4

    public static void Shortcut(Process owner, nint window, ushort modifier, ushort key)
    {
        Focus(owner, window);
        INPUT[] inputs = [Key(modifier, false), Key(key, false), Key(key, true), Key(modifier, true)];
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            if (sent > 0) SendInput(2, [Key(key, true), Key(modifier, true)], Marshal.SizeOf<INPUT>());
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The native keyboard action was not fully delivered.");
        }
    }

    public static void Capture(Process owner, nint window, string path)
    {
        Focus(owner, window);
        nint previousDpi = SetThreadDpiAwarenessContext(-4); // This caller thread only; restore below.
        nint windowDc = 0, memoryDc = 0, bitmap = 0, previousBitmap = 0;
        try
        {
            if (!GetWindowRect(window, out RECT rectangle)) throw new Win32Exception();
            int width = rectangle.Right - rectangle.Left, height = rectangle.Bottom - rectangle.Top;
            if (width is < 100 or > 8192 || height is < 100 or > 8192)
                throw new InvalidDataException("The native window has invalid capture bounds.");
            windowDc = GetWindowDC(window);
            if (windowDc == 0) throw new Win32Exception();
            memoryDc = CreateCompatibleDC(windowDc);
            bitmap = CreateCompatibleBitmap(windowDc, width, height);
            if (memoryDc == 0 || bitmap == 0) throw new Win32Exception();
            previousBitmap = SelectObject(memoryDc, bitmap);
            if (previousBitmap == 0 || previousBitmap == -1) throw new Win32Exception();
            if (!PrintWindow(window, memoryDc, 2)) throw new Win32Exception(0, "Native window capture failed.");
            SelectObject(memoryDc, previousBitmap); previousBitmap = 0;
            var info = new BITMAPINFO { Header = new()
            {
                Size = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), Width = width, Height = height,
                Planes = 1, BitCount = 32, SizeImage = checked((uint)(width * height * 4))
            } };
            byte[] pixels = new byte[info.Header.SizeImage];
            if (GetDIBits(windowDc, bitmap, 0, (uint)height, pixels, ref info, 0) != height)
                throw new Win32Exception();
            Validate(owner, window);
            // Capture the client surface, not only the title bar, in the blank-image guard.
            int first = (height / 3 * width + width / 3) * 4;
            bool varied = false;
            for (int y = height / 4; y < height * 3 / 4 && !varied; y++)
                for (int x = width / 4; x < width * 3 / 4; x++)
                {
                    int p = (y * width + x) * 4;
                    if (pixels[p] != pixels[first] || pixels[p + 1] != pixels[first + 1] || pixels[p + 2] != pixels[first + 2])
                    { varied = true; break; }
                }
            if (!varied) throw new InvalidDataException("Native capture has a uniform client area; rendering is unproven.");
            using var writer = new BinaryWriter(File.Create(path));
            writer.Write((ushort)0x4d42); writer.Write(checked(54 + pixels.Length)); writer.Write(0); writer.Write(54);
            writer.Write(40); writer.Write(width); writer.Write(height); writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write(0); writer.Write(pixels.Length); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
            writer.Write(pixels);
        }
        finally
        {
            if (previousBitmap != 0 && previousBitmap != -1 && memoryDc != 0) SelectObject(memoryDc, previousBitmap);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memoryDc != 0) DeleteDC(memoryDc);
            if (windowDc != 0) ReleaseDC(window, windowDc);
            if (previousDpi != 0) SetThreadDpiAwarenessContext(previousDpi);
        }
    }

    private static void Focus(Process owner, nint window)
    {
        Validate(owner, window);
        if (IsIconic(window)) ShowWindow(window, 9);
        SetForegroundWindow(window);
        Validate(owner, window);
        if (GetForegroundWindow() != window)
            throw new InvalidOperationException("The owned native window could not receive foreground input.");
    }

    private static void Validate(Process owner, nint window)
    {
        RequireWindows();
        if (owner.HasExited || window == 0 || !IsWindow(window))
            throw new InvalidOperationException("The owned native window is no longer available.");
        GetWindowThreadProcessId(window, out uint pid);
        if (pid != owner.Id || !IsWindowVisible(window))
            throw new InvalidOperationException("The window does not belong to the requested live process.");
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native Windows UI execution is required.");
    }

    private static INPUT Key(ushort key, bool up) => new()
    { Type = 1, Data = new() { Keyboard = new() { VirtualKey = key, Flags = up ? 2U : 0U } } };

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint Type; public INPUTDATA Data; }
    [StructLayout(LayoutKind.Explicit)] private struct INPUTDATA
    { [FieldOffset(0)] public KEYBDINPUT Keyboard; [FieldOffset(0)] public MOUSEINPUT Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT
    { public ushort VirtualKey, ScanCode; public uint Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT
    { public int X, Y; public uint MouseData, Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFOHEADER
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPixelsPerMeter, YPixelsPerMeter; public uint ColorsUsed, ColorsImportant;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFO { public BITMAPINFOHEADER Header; public uint Colors; }
    private delegate bool EnumWindow(nint window, nint parameter);
    [DllImport("user32", SetLastError = true)] private static extern bool EnumWindows(EnumWindow callback, nint parameter);
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint window, StringBuilder title, int maximum);
    [DllImport("user32")] private static extern bool IsWindow(nint window);
    [DllImport("user32")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32")] private static extern bool IsIconic(nint window);
    [DllImport("user32")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32")] private static extern nint GetForegroundWindow();
    [DllImport("user32", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32", SetLastError = true)] private static extern bool GetWindowRect(nint window, out RECT rectangle);
    [DllImport("user32", SetLastError = true)] private static extern nint GetWindowDC(nint window);
    [DllImport("user32")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("user32", SetLastError = true)] private static extern bool PrintWindow(nint window, nint dc, uint flags);
    [DllImport("gdi32", SetLastError = true)] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32", SetLastError = true)] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32", SetLastError = true)] private static extern nint SelectObject(nint dc, nint item);
    [DllImport("gdi32")] private static extern bool DeleteObject(nint item);
    [DllImport("gdi32")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32", SetLastError = true)] private static extern int GetDIBits(nint dc, nint bitmap, uint start,
        uint count, byte[] data, ref BITMAPINFO info, uint usage);
}
