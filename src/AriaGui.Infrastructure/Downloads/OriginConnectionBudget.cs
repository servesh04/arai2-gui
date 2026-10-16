namespace AriaGui.Infrastructure.Downloads;

// A response-lifetime budget uses the actual redirect hop hostname, not the
// original URL or the loopback gateway host seen by aria2. SocketsHttpHandler
// can retain a socket after response disposal, so its ConnectCallback is too
// late a release boundary for bounded request admission.
internal static class OriginConnectionBudget
{
    private static readonly SemaphoreSlim Global = new(16, 16);
    private static readonly object Sync = new();
    private static readonly Dictionary<string, OriginGate> Origins = new(StringComparer.OrdinalIgnoreCase);
    private sealed class OriginGate { internal readonly SemaphoreSlim Semaphore = new(4, 4); internal int Users; }

    internal static async Task<IDisposable> AcquireAsync(string host, int port, CancellationToken token)
    {
        await Global.WaitAsync(token).ConfigureAwait(false);
        var key = host + ":" + port;
        OriginGate origin;
        lock (Sync)
        {
            if (!Origins.TryGetValue(key, out var existing)) Origins.Add(key, origin = new OriginGate());
            else origin = existing;
            origin.Users++;
        }
        try { await origin.Semaphore.WaitAsync(token).ConfigureAwait(false); }
        catch { ReleaseReference(key, origin); Global.Release(); throw; }
        return new Lease(key, origin);
    }
    private static void ReleaseReference(string key, OriginGate origin)
    {
        lock (Sync)
            if (--origin.Users == 0) { Origins.Remove(key); origin.Semaphore.Dispose(); }
    }
    private sealed class Lease(string key, OriginGate origin) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            origin.Semaphore.Release(); ReleaseReference(key, origin); Global.Release();
        }
    }
}
internal sealed class BudgetedResponseContent : HttpContent
{
    private readonly HttpContent _inner;
    private readonly IDisposable _lease;
    private int _disposed;
    internal BudgetedResponseContent(HttpContent inner, IDisposable lease)
    {
        _inner = inner; _lease = lease;
        foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
    }
    protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => _inner.CopyToAsync(stream);
    protected override bool TryComputeLength(out long length)
    {
        if (_inner.Headers.ContentLength is long known) { length = known; return true; }
        length = 0; return false;
    }
    protected override Task<Stream> CreateContentReadStreamAsync() => _inner.ReadAsStreamAsync();
    protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token) => _inner.ReadAsStreamAsync(token);
    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        { try { if (disposing) _inner.Dispose(); } finally { _lease.Dispose(); } }
        base.Dispose(disposing);
    }
}
