using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace AriaGui.Infrastructure.Tests;

// Private direct-mode fixture: count bytes read from the origin socket before
// forwarding to aria2. This is upstream traffic, not aria2 completedLength.
internal sealed class CountingTcpRelay : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<Task> _connections = [];
    private readonly int _originPort;
    private readonly Task _accept;
    private long _originBytes;

    public CountingTcpRelay(string originUrl)
    {
        var origin = new Uri(originUrl);
        _originPort = origin.Port;
        _listener.Start();
        Url = new UriBuilder(origin) { Port = ((IPEndPoint)_listener.LocalEndpoint).Port }.Uri.AbsoluteUri;
        _accept = AcceptAsync();
    }

    public string Url { get; }
    public long OriginBytes => Interlocked.Read(ref _originBytes);

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
                _connections.Add(ServeAsync(await _listener.AcceptTcpClientAsync(_stop.Token)));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        using (var origin = new TcpClient())
        {
            try
            {
                await origin.ConnectAsync(IPAddress.Loopback, _originPort, _stop.Token);
                var request = PumpAsync(client.GetStream(), origin.GetStream(), false);
                var response = PumpAsync(origin.GetStream(), client.GetStream(), true);
                await Task.WhenAny(request, response);
                client.Close(); origin.Close();
                try { await Task.WhenAll(request, response); } catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException) { }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException or OperationCanceledException) { }
        }
    }

    private async Task PumpAsync(Stream source, Stream destination, bool count)
    {
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, _stop.Token);
            if (read == 0) return;
            if (count) Interlocked.Add(ref _originBytes, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), _stop.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Stop();
        await _accept;
        await Task.WhenAll(_connections);
        _stop.Dispose();
    }
}
