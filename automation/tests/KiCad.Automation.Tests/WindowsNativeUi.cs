using System.ComponentModel;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
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

    public static async Task WaitForClosedWindow(Process owner, nint window, CancellationToken token)
    {
        RequireWindows();
        while (!owner.HasExited && IsWindow(window))
        {
            GetWindowThreadProcessId(window, out uint pid);
            if (pid != owner.Id) return; // The original window closed; never act on a reused handle.
            await Task.Delay(250, token);
        }
    }

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

    public static void Click(Process owner, nint window, int screenX, int screenY, bool cancelBeforeRelease = false)
    {
        Focus(owner, window);
        nint previousDpi = SetThreadDpiAwarenessContext(-4);
        try
        {
            if (!GetWindowRect(window, out RECT rect) || screenX < rect.Left || screenX >= rect.Right
                || screenY < rect.Top || screenY >= rect.Bottom)
                throw new ArgumentException("The pointer target must be inside the exact owned window.");
            if (!SetCursorPos(screenX, screenY)) throw new Win32Exception();
            if (WindowFromPoint(new POINT { X = screenX, Y = screenY }) != window)
                throw new InvalidOperationException("Another window obscures the requested caption target.");
            INPUT down = new() { Data = new() { Mouse = new() { Flags = 2 } } };
            INPUT up = new() { Data = new() { Mouse = new() { Flags = 4 } } };
            INPUT[] input = cancelBeforeRelease ? [down, Key(0x1b, false), Key(0x1b, true), up] : [down, up];
            uint sent = SendInput((uint)input.Length, input, Marshal.SizeOf<INPUT>());
            if (sent != input.Length)
            {
                if (sent > 0) SendInput(2, [Key(0x1b, true), up], Marshal.SizeOf<INPUT>());
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The native pointer action was not fully delivered.");
            }
        }
        finally { if (previousDpi != 0) SetThreadDpiAwarenessContext(previousDpi); }
    }

    public static void PressKey(Process owner, nint window, ushort key)
    {
        Validate(owner, window);
        if (GetForegroundWindow() != window) throw new InvalidOperationException("The owned window must have keyboard focus.");
        INPUT[] inputs = [Key(key, false), Key(key, true)];
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            if (sent > 0) SendInput(1, [inputs[1]], Marshal.SizeOf<INPUT>());
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The native key was not fully delivered.");
        }
    }

    public static void ClickButton(Process owner, nint window, nint button, string expectedText)
    {
        Focus(owner, window); Validate(owner, button);
        if (!IsChild(window, button) || !IsWindowVisible(button) || !IsWindowEnabled(button))
            throw new InvalidOperationException("The native button is not ready in the requested window.");
        var name = new StringBuilder(1024); GetWindowTextW(button, name, name.Capacity);
        if (name.ToString().Replace("&", "", StringComparison.Ordinal) != expectedText)
            throw new InvalidOperationException("The native button label changed before input.");
        nint previousDpi = SetThreadDpiAwarenessContext(-4);
        try
        {
            if (!GetWindowRect(button, out RECT rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
                throw new InvalidDataException("The native button has no visible bounds.");
            int x = rect.Left + (rect.Right - rect.Left) / 2, y = rect.Top + (rect.Bottom - rect.Top) / 2;
            if (!SetCursorPos(x, y)) throw new Win32Exception();
            if (WindowFromPoint(new POINT { X = x, Y = y }) != button)
                throw new InvalidOperationException("Another control obscures the requested native button.");
            INPUT[] inputs = [new() { Data = new() { Mouse = new() { Flags = 2 } } }, new() { Data = new() { Mouse = new() { Flags = 4 } } }];
            uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            if (sent != inputs.Length)
            {
                if (sent > 0) SendInput(1, [inputs[1]], Marshal.SizeOf<INPUT>());
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The native button click was not fully delivered.");
            }
        }
        finally { if (previousDpi != 0) SetThreadDpiAwarenessContext(previousDpi); }
    }

    public static async Task WaitForSystemMenu(Process owner, nint window, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested(); Validate(owner, window);
            uint thread = GetWindowThreadProcessId(window, out _);
            var info = new GUITHREADINFO { Size = (uint)Marshal.SizeOf<GUITHREADINFO>() };
            if (!GetGUIThreadInfo(thread, ref info)) throw new Win32Exception();
            if ((info.Flags & 8) != 0 && info.MenuOwner == window) return;
            await Task.Delay(25, deadline.Token);
        }
    }

    public static async Task SelectSystemMenuItem(Process owner, nint window, string label, CancellationToken token)
    {
        await WaitForSystemMenu(owner, window, token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        nint menu = GetSystemMenu(window, false); int count = GetMenuItemCount(menu);
        if (menu == 0 || count is < 1 or > 64) throw new InvalidDataException("The native system menu is unavailable.");
        int target = -1;
        for (int index = 0; index < count; ++index)
        {
            var text = new StringBuilder(512); GetMenuStringW(menu, (uint)index, text, text.Capacity, 0x400);
            if (text.ToString().Replace("&", "", StringComparison.Ordinal) != label) continue;
            if (target >= 0) throw new InvalidDataException("The system menu action is ambiguous.");
            target = index;
        }
        if (target < 0) throw new InvalidDataException("The expected native system menu action is absent.");
        for (int moves = 0; moves <= count; ++moves)
        {
            Validate(owner, window); deadline.Token.ThrowIfCancellationRequested();
            uint state = GetMenuState(menu, (uint)target, 0x400);
            if (state == uint.MaxValue || (state & 3) != 0) throw new InvalidDataException("The system menu action is unavailable.");
            if ((state & 0x80) != 0) return; // MF_HILITE: observed selection, not an assumed key effect.
            string before = Highlight();
            PressKey(owner, window, 0x26); // Documented Up-arrow navigation, including wraparound.
            while (Highlight() == before) await Task.Delay(25, deadline.Token);
        }
        throw new InvalidDataException("Keyboard navigation did not select the native system menu action.");

        string Highlight() => string.Join(',', Enumerable.Range(0, count).Where(index =>
            (GetMenuState(menu, (uint)index, 0x400) & 0x80) != 0));
    }

    public static void Capture(Process owner, nint window, string path)
    {
        Validate(owner, window);
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
            WritePng(varied ? path : path + ".rejected.png", width, height, pixels);
            if (!varied) throw new InvalidDataException("Native capture has a uniform client area; rejected pixels were retained for diagnosis.");
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

    // Encode the unmodified bottom-up GDI pixels as ordinary RGB PNG so the
    // evidence can be inspected in the same viewers as native MCP renders.
    internal static void WritePng(string path, int width, int height, byte[] bottomUpBgra)
    {
        if (width <= 0 || height <= 0 || bottomUpBgra.Length != checked(width * height * 4))
            throw new ArgumentException("Invalid native capture dimensions.");
        using var file = File.Create(path);
        file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 2;
        Chunk("IHDR"u8, header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            byte[] row = new byte[checked(width * 3 + 1)];
            for (int y = height - 1; y >= 0; y--)
            {
                for (int x = 0; x < width; x++)
                {
                    int source = (y * width + x) * 4, target = x * 3 + 1;
                    row[target] = bottomUpBgra[source + 2]; row[target + 1] = bottomUpBgra[source + 1];
                    row[target + 2] = bottomUpBgra[source];
                }
                zlib.Write(row);
            }
        }
        Chunk("IDAT"u8, compressed.ToArray());
        Chunk("IEND"u8, []);

        void Chunk(ReadOnlySpan<byte> type, byte[] data)
        {
            Span<byte> number = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(number, data.Length); file.Write(number);
            file.Write(type); file.Write(data);
            uint crc = 0xffffffff;
            foreach (byte value in type) Append(value);
            foreach (byte value in data) Append(value);
            BinaryPrimitives.WriteUInt32BigEndian(number, ~crc); file.Write(number);
            void Append(byte value)
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0U : 0xedb88320U);
            }
        }
    }

    private static void Focus(Process owner, nint window)
    {
        Validate(owner, window);
        if (IsIconic(window)) ShowWindow(window, 9);
        SetForegroundWindow(window);
        // Cross-input-queue foreground activation completes asynchronously.
        // A bounded no-op message waits for that window to process the nudge;
        // never attach input queues or weaken the final ownership/focus check.
        if (SendMessageTimeoutW(window, 0, 0, 0, 2, 5000, out _) == 0)
            throw new InvalidOperationException("The owned native window did not acknowledge foreground activation.");
        Validate(owner, window);
        if (GetForegroundWindow() != window)
            throw new InvalidOperationException("The owned native window could not receive foreground input.");
    }

    internal static nint ObservedForegroundWindow => GetForegroundWindow();

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

    private static INPUT Key(ushort key, bool up)
    {
        // Native menu navigation needs an ordinary physical-key packet, not a
        // virtual-key-only message with a zero scan code. Preserve E0 prefixes.
        uint scan = MapVirtualKeyW(key, 4); // MAPVK_VK_TO_VSC_EX
        if ((scan & 0xff) == 0 || (scan >> 8) is not (0 or 0xe0))
            throw new ArgumentException("This key has no supported native scan-code mapping.", nameof(key));
        // Some layouts map navigation VKs to the keypad scan code without an
        // E0 prefix. Preserve the dedicated navigation key, not keypad End/1.
        bool extended = (scan >> 8) == 0xe0 || key is >= 0x21 and <= 0x28 or 0x2d or 0x2e;
        return new() { Type = 1, Data = new() { Keyboard = new() { ScanCode = (ushort)(scan & 0xff),
            Flags = 8U | (up ? 2U : 0U) | (extended ? 1U : 0U) } } };
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct GUITHREADINFO
    { public uint Size, Flags; public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret; public RECT CaretRectangle; }
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
    [DllImport("user32", SetLastError = true)] private static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
    [DllImport("user32")] private static extern nint GetSystemMenu(nint window, bool revert);
    [DllImport("user32")] private static extern int GetMenuItemCount(nint menu);
    [DllImport("user32")] private static extern uint GetMenuState(nint menu, uint item, uint flags);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int GetMenuStringW(nint menu, uint item, StringBuilder text, int maximum, uint flags);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint window, StringBuilder title, int maximum);
    [DllImport("user32")] private static extern bool IsWindow(nint window);
    [DllImport("user32")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32")] private static extern bool IsWindowEnabled(nint window);
    [DllImport("user32")] private static extern bool IsChild(nint parent, nint child);
    [DllImport("user32")] private static extern bool IsIconic(nint window);
    [DllImport("user32")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32")] private static extern nint GetForegroundWindow();
    [DllImport("user32", SetLastError = true)] private static extern nint SendMessageTimeoutW(nint window, uint message,
        nuint wParam, nint lParam, uint flags, uint timeout, out nuint result);
    [DllImport("user32", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32")] private static extern uint MapVirtualKeyW(uint key, uint mode);
    [DllImport("user32", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32")] private static extern nint WindowFromPoint(POINT point);
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
