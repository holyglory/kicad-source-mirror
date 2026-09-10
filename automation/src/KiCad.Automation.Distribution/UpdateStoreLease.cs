namespace KiCad.Automation.Distribution;

// One local installation owner at a time. This is data ownership, not a job or
// capacity scheduler. Keep the lock inode after release; never unlink it.
internal static class UpdateStoreLease
{
    public static async Task<FileStream> AcquireAsync(string path, CancellationToken token, TimeSpan? maximumWait = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(maximumWait ?? TimeSpan.FromMinutes(2));
        int delay = 25;
        while (true)
        {
            try
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException("An update store lock cannot be redirected.");
                FileStream lease;
                try { lease = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException error) when (Contention(error))
                {
                    await Task.Delay(delay, deadline.Token); delay = Math.Min(delay * 2, 500); continue;
                }
                if (deadline.IsCancellationRequested) { lease.Dispose(); deadline.Token.ThrowIfCancellationRequested(); }
                return lease;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new IOException("The update store remained busy beyond its bounded wait."); }
        }
    }
    internal static bool Contention(IOException error) => (error.HResult & 0xffff) is 32 or 33 or 11 or 35;
}
