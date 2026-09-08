using System.Threading.Channels;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

[Flags]
public enum DesignFileChangeReason
{
    InitialStateRequired = 1,
    FileChanged = 2,
    RecoveryRequired = 4,
    ObservationStopped = 8
}

public sealed record DesignFileChange(string Path, ulong Sequence, DesignFileChangeReason Reasons,
    string? ErrorCode);

/// <summary>
/// Loss-aware notification source for one engineering XML file. Notifications are read hints,
/// never saved/valid XML or permission to edit KiCad. The consumer must read and validate the
/// complete file, compare its content with the synchronized base, and preserve competing edits.
/// A burst occupies one pending slot; recovery/failure flags cannot be overwritten by later saves.
/// Attach before the initial read. Cancellation cancels a wait, not this subscription or editors.
/// </summary>
public sealed class DesignFileSubscription : IDisposable
{
    private readonly object gate = new();
    private readonly Channel<byte> ready = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        { SingleReader = false, SingleWriter = false, AllowSynchronousContinuations = false });
    private readonly FileSystemWatcher watcher;
    private readonly FileSystemWatcher? directoryWatcher;
    private readonly string directory;
    private readonly StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private DesignFileChangeReason pending;
    private string? errorCode;
    private ulong sequence;
    private bool stopped;
    private bool disposed;

    public string Path { get; }

    public DesignFileSubscription(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        directory = System.IO.Path.GetDirectoryName(Path)
            ?? throw new AutomationException("invalid_sync_path", "A design file requires an existing parent directory.");
        if (string.IsNullOrEmpty(System.IO.Path.GetFileName(Path)) || Directory.Exists(Path))
            throw new AutomationException("invalid_sync_path", "Choose an engineering XML file, not a directory.");
        watcher = new FileSystemWatcher(directory)
        {
            // Watch names as well as writes: editors commonly save by renaming a temporary file.
            // Filtering ourselves also lets rename-away invalidate the original target.
            Filter = "*", IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        try
        {
            watcher.Changed += Changed;
            watcher.Created += Changed;
            watcher.Deleted += Changed;
            watcher.Renamed += Renamed;
            watcher.Error += Error;
            // A watch can remain attached to the old directory after it is replaced. Stop rather
            // than claiming the new design path is still observed. Reattach then read a fresh base.
            string? parent = System.IO.Path.GetDirectoryName(directory);
            if (parent is not null)
            {
                directoryWatcher = new FileSystemWatcher(parent)
                    { Filter = "*", NotifyFilter = NotifyFilters.DirectoryName };
                directoryWatcher.Created += DirectoryChanged;
                directoryWatcher.Deleted += DirectoryChanged;
                directoryWatcher.Renamed += DirectoryRenamed;
                directoryWatcher.Error += Error;
                directoryWatcher.EnableRaisingEvents = true;
            }
            watcher.EnableRaisingEvents = true;
            Publish(DesignFileChangeReason.InitialStateRequired);
        }
        catch
        {
            directoryWatcher?.Dispose();
            watcher.Dispose();
            throw;
        }
    }

    public async Task<DesignFileChange> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        while (await ready.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                if (!ready.Reader.TryRead(out _)) continue;
                var result = new DesignFileChange(Path, sequence, pending, errorCode);
                pending = 0;
                errorCode = null;
                return result;
            }
        }
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            throw new AutomationException("sync_watch_stopped", "Reattach the file observer and read current XML before synchronizing.");
        }
    }

    private void Changed(object sender, FileSystemEventArgs args) => NotifyChange(args.FullPath);
    private void Renamed(object sender, RenamedEventArgs args) => NotifyChange(args.FullPath, args.OldFullPath);
    private void Error(object sender, ErrorEventArgs args) => NotifyError(args.GetException());
    private void DirectoryChanged(object sender, FileSystemEventArgs args) => DirectoryChange(args.FullPath);
    private void DirectoryRenamed(object sender, RenamedEventArgs args) => DirectoryChange(args.FullPath, args.OldFullPath);

    private void DirectoryChange(string path, string? oldPath = null)
    {
        if (string.Equals(path, directory, comparison) || string.Equals(oldPath, directory, comparison))
            Publish(DesignFileChangeReason.RecoveryRequired | DesignFileChangeReason.ObservationStopped,
                "sync_directory_changed");
    }

    // Internal entry points let tests exercise overflow and coalescing without relying on a
    // particular kernel buffer size. Real filesystem journeys verify event wiring separately.
    internal void NotifyChange(string path, string? oldPath = null)
    {
        if (string.Equals(path, Path, comparison) || string.Equals(oldPath, Path, comparison))
            Publish(DesignFileChangeReason.FileChanged);
    }

    internal void NotifyError(Exception error) => Publish(
        DesignFileChangeReason.RecoveryRequired | DesignFileChangeReason.ObservationStopped,
        error is InternalBufferOverflowException ? "sync_watch_overflow" : "sync_watch_failed");

    private void Publish(DesignFileChangeReason reason, string? code = null)
    {
        lock (gate)
        {
            if (disposed || stopped) return;
            sequence++;
            pending |= reason;
            errorCode ??= code;
            ready.Writer.TryWrite(0);
            if ((reason & DesignFileChangeReason.ObservationStopped) != 0)
            {
                stopped = true;
                ready.Writer.TryComplete();
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            ready.Writer.TryComplete();
        }
        // Do not hold the gate while shutting down native callbacks.
        directoryWatcher?.Dispose();
        watcher.Dispose();
    }
}
