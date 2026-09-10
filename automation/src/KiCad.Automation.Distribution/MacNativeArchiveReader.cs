using System.Formats.Tar;
using System.Reflection;
using System.Runtime.InteropServices;

namespace KiCad.Automation.Distribution;

internal sealed record NativeMacArchiveHeader(string Name, TarEntryType EntryType, long Length,
    UnixFileMode Mode, string LinkName, int ExtendedAttributeCount);

/// <summary>Read original archive bytes through the system libarchive C API.
/// No extraction, filesystem writes or executable loading from the archive.</summary>
internal sealed class MacNativeArchiveReader : IDisposable
{
    private const string Import = "kicad-system-archive";
    private static readonly Lazy<nint> Library = new(() => NativeLibrary.Load(OperatingSystem.IsMacOS()
        ? "/usr/lib/libarchive.dylib" : OperatingSystem.IsLinux() ? "libarchive.so.13"
        : throw new PlatformNotSupportedException("Mac archive inspection requires macOS or the Linux verification host.")));
    private static readonly ReadCallback Read = ReadBlock;
    private readonly State state;
    private GCHandle client;
    private nint archive;

    static MacNativeArchiveReader() => NativeLibrary.SetDllImportResolver(typeof(MacNativeArchiveReader).Assembly,
        (string name, Assembly _, DllImportSearchPath? _) => name == Import ? Library.Value : 0);

    public MacNativeArchiveReader(Stream stream, CancellationToken token)
    {
        state = new State(stream, token).Initialize();
        client = GCHandle.Alloc(state);
        try
        {
            archive = archive_read_new();
            if (archive == 0) throw new IOException("Could not create the native Mac archive reader.");
            Check(archive_read_support_filter_gzip(archive));
            Check(archive_read_support_format_tar(archive));
            // Inspect AppleDouble sidecars explicitly instead of hiding them in
            // the following owner's metadata. Native extraction later applies them.
            Check(archive_read_set_format_option(archive, "tar", "mac-ext", null));
            Check(archive_read_open2(archive, GCHandle.ToIntPtr(client), 0, Read, 0, 0));
        }
        catch { Dispose(); throw; }
    }

    public NativeMacArchiveHeader? Next()
    {
        state.Token.ThrowIfCancellationRequested();
        int status = archive_read_next_header(archive, out nint entry);
        if (status == 1) return null; // ARCHIVE_EOF, not an error or success substitute.
        Check(status);
        if (archive_filter_code(archive, 0) != 1)
            throw new InvalidDataException("Mac update archives must use gzip compression.");
        archive_entry_fflags(entry, out nuint set, out nuint clear);
        if (set != 0 || clear != 0 || archive_entry_sparse_count(entry) != 0)
            throw new InvalidDataException("Sparse data or filesystem flags are unsupported in Mac updates.");
        string name = Text(archive_entry_pathname_utf8(entry));
        string link = "";
        TarEntryType type = archive_entry_filetype(entry) switch
        {
            0x4000 => TarEntryType.Directory, 0x8000 => TarEntryType.RegularFile,
            0xa000 => TarEntryType.SymbolicLink, _ => throw new InvalidDataException("Unsupported native Mac archive file type.")
        };
        if (archive_entry_hardlink_utf8(entry) != 0) type = TarEntryType.HardLink;
        if (type == TarEntryType.SymbolicLink) link = Text(archive_entry_symlink_utf8(entry));
        return new(name, type, archive_entry_size(entry), (UnixFileMode)archive_entry_perm(entry), link,
            archive_entry_xattr_count(entry));
    }

    public int ReadData(byte[] buffer)
    {
        state.Token.ThrowIfCancellationRequested();
        nint count = archive_read_data(archive, buffer, (nuint)buffer.Length);
        if (count < 0) Check(-30);
        return checked((int)count);
    }

    private void Check(int status)
    {
        if (state.Error is OperationCanceledException cancelled) throw cancelled;
        if (state.Error is not null) throw new IOException("Could not read Mac archive bytes.", state.Error);
        if (status != 0)
        {
            string message = Marshal.PtrToStringUTF8(archive_error_string(archive)) ?? "Unknown native archive error";
            throw new InvalidDataException("Mac archive parser rejected the input: " + message[..Math.Min(1024, message.Length)]);
        }
    }

    private static string Text(nint pointer) => Marshal.PtrToStringUTF8(pointer)
        ?? throw new InvalidDataException("Mac archive path cannot be represented as UTF-8.");

    private static nint ReadBlock(nint _, nint data, out nint buffer)
    {
        var state = (State)GCHandle.FromIntPtr(data).Target!;
        buffer = state.Pin.AddrOfPinnedObject();
        try
        {
            state.Token.ThrowIfCancellationRequested();
            return state.Stream.ReadAsync(state.Buffer.AsMemory(), state.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception error) { state.Error = error; return -1; }
    }

    public void Dispose()
    {
        if (archive != 0) { archive_read_free(archive); archive = 0; }
        if (client.IsAllocated) client.Free();
        if (state.Pin.IsAllocated) state.Pin.Free();
    }

    private sealed class State(Stream stream, CancellationToken token)
    {
        public Stream Stream { get; } = stream;
        public CancellationToken Token { get; } = token;
        public byte[] Buffer { get; } = new byte[128 * 1024];
        public GCHandle Pin = default;
        public Exception? Error;
        public State Initialize() { Pin = GCHandle.Alloc(Buffer, GCHandleType.Pinned); return this; }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint ReadCallback(nint archive, nint data, out nint buffer);
    [DllImport(Import, CallingConvention = CallingConvention.Cdecl)] private static extern nint archive_read_new();
    [DllImport(Import, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_read_support_filter_gzip(nint archive);
    [DllImport(Import, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_read_support_format_tar(nint archive);
    [DllImport(Import, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_read_set_format_option(nint archive,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string module, [MarshalAs(UnmanagedType.LPUTF8Str)] string option,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? value);
    [DllImport(Import, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_read_open2(nint archive, nint data,
        nint open, ReadCallback read, nint skip, nint close);
    [DllImport(Import, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_read_next_header(nint archive, out nint entry);
    [DllImport(Import, CallingConvention = CallingConvention.Cdecl)] private static extern nint archive_read_data(nint archive, [Out] byte[] buffer, nuint bytes);
    [DllImport(Import, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_read_free(nint archive);
    [DllImport(Import, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_filter_code(nint archive, int filter);
    [DllImport(Import, CallingConvention = CallingConvention.Cdecl)] private static extern nint archive_error_string(nint archive);
    [DllImport(Import, CallingConvention = CallingConvention.Cdecl)] private static extern nint archive_entry_pathname_utf8(nint entry);
    [DllImport(Import, CallingConvention = CallingConvention.Cdecl)] private static extern uint archive_entry_filetype(nint entry);
    [DllImport(Import, CallingConvention = CallingConvention.Cdecl)] private static extern uint archive_entry_perm(nint entry);
    [DllImport(Import, CallingConvention = CallingConvention.Cdecl)] private static extern long archive_entry_size(nint entry);
    [DllImport(Import, CallingConvention = CallingConvention.Cdecl)] private static extern nint archive_entry_symlink_utf8(nint entry);
    [DllImport(Import, CallingConvention = CallingConvention.Cdecl)] private static extern nint archive_entry_hardlink_utf8(nint entry);
    [DllImport(Import, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_entry_sparse_count(nint entry);
    [DllImport(Import, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_entry_xattr_count(nint entry);
    [DllImport(Import, CallingConvention = CallingConvention.Cdecl)] private static extern void archive_entry_fflags(nint entry, out nuint set, out nuint clear);
}
