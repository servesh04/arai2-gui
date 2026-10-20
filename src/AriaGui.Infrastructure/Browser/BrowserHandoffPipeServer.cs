using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using AriaGui.Application.Downloads;

namespace AriaGui.Infrastructure.Browser;

public sealed class BrowserHandoffPipeServer(BrowserHandoffService handoff, string hostExecutable,
    CancellationToken applicationLifetime) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(applicationLifetime);
    private readonly List<NamedPipeServerStream> _pipes = [];
    private Task[] _workers = [];

    public Task StartAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new IOException("User identity unavailable.");
        var pipeName = BrowserHandoffProtocol.PipeName(sid);
        for (var index = 0; index < 2; index++)
            _pipes.Add(PrivateBrowserPipe.Create(pipeName, sid, index == 0, 2));
        _workers = _pipes.Select(pipe => Task.Run(() => AcceptAsync(pipe, sid), token)).ToArray();
        return Task.CompletedTask;
    }

    private async Task AcceptAsync(NamedPipeServerStream pipe, string sid)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                if (!PrivateBrowserPipe.VerifyPeer(pipe, sid, hostExecutable)) continue;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                var lengthBytes = new byte[4];
                await pipe.ReadExactlyAsync(lengthBytes, deadline.Token).ConfigureAwait(false);
                var size = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
                BrowserHandoffResponse response;
                if (size is < 1 or > BrowserHandoffProtocol.MaximumPipeBytes)
                    response = new(false, Error: "invalid-request");
                else
                {
                    var body = new byte[size];
                    await pipe.ReadExactlyAsync(body, deadline.Token).ConfigureAwait(false);
                    response = BrowserHandoffProtocol.IsHello(body) ? await handoff.HelloAsync(deadline.Token).ConfigureAwait(false)
                        : BrowserHandoffProtocol.TryParse(body, out var request)
                            ? await handoff.HandleAsync(request!, deadline.Token).ConfigureAwait(false)
                            : new(false, Error: "invalid-request");
                }
                var result = response.ToJson();
                BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, result.Length);
                await pipe.WriteAsync(lengthBytes, deadline.Token).ConfigureAwait(false);
                await pipe.WriteAsync(result, deadline.Token).ConfigureAwait(false);
                await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
                // Keep the instance connected until the host has consumed the response.
                var acknowledgment = new byte[1];
                await pipe.ReadAsync(acknowledgment, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            finally { try { if (pipe.IsConnected) pipe.Disconnect(); } catch (Exception) { } }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        foreach (var pipe in _pipes) pipe.Dispose();
        try { await Task.WhenAll(_workers).WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { }
        _stop.Dispose();
    }
}
