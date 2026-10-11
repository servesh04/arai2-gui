using AriaGui.Application.Engine;

namespace AriaGui.Infrastructure.Aria2;

// FileShare.None is process-safe and releases automatically if the GUI crashes.
public sealed class SingleInstanceLease : IDisposable
{
    private readonly FileStream? _handle;
    private int _disposed;
    public bool IsPrimary => _handle is not null && Volatile.Read(ref _disposed) == 0;
    public string RuntimeDirectory { get; }
    public SingleInstanceLease(string runtimeDirectory)
    {
        RuntimeDirectory = Path.GetFullPath(runtimeDirectory);
        UserRuntimeDirectory.EnsurePrivate(runtimeDirectory);
        try { _handle = new FileStream(Path.Combine(runtimeDirectory, "app.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { /* Another instance holds the file. */ }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new EngineException(EngineFailure.StorageUnavailable, "The app could not open its private instance lock. Check folder access."); }
    }
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _handle?.Dispose(); }
}
