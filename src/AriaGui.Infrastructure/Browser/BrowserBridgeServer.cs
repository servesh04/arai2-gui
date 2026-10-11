using System.IO.Pipes;
using AriaGui.Application.Downloads;
using AriaGui.Browser.Contracts;
using AriaGui.Core.Downloads;
using AriaGui.Infrastructure.Aria2;

namespace AriaGui.Infrastructure.Browser;

public enum BrowserBridgeFailure { None, Initialization, PeerDenied, Protocol, Session, DrainDeadline }

/// <summary>Private application service. No engine RPC or UI dispatcher access. Production composition remains disabled.</summary>
public sealed class BrowserBridgeServer : IAsyncDisposable
{
    public const uint MaximumSessions = 4;
    public const int MaximumRequestsPerSession = 8;
    private const string TrustedCaller = "browser-native-host-v1";
    private readonly ExternalDownloadIntakeService _intake;
    private readonly DownloadPreferencesService _preferences;
    private readonly SingleInstanceLease _lease;
    private readonly BrowserBridgeEndpoint _endpoint;
    private readonly string _peerExecutable;
    private readonly CancellationToken _applicationLifetime;
    private readonly CancellationTokenSource _receive = new();
    private readonly object _admission = new();
    private readonly List<NamedPipeServerStream> _pipes = [];
    private Task[] _workers = [];
    private bool _running, _started;
    private int _failure;
    private int _disposed;
    public BrowserBridgeFailure LastFailure => (BrowserBridgeFailure)Volatile.Read(ref _failure);
    public bool IsRunning { get { lock (_admission) return _running; } }
    public BrowserBridgeEndpoint Endpoint => _endpoint;
    public BrowserBridgeServer(ExternalDownloadIntakeService intake, DownloadPreferencesService preferences,
        SingleInstanceLease lease, BrowserBridgeEndpoint endpoint, string trustedPeerExecutable, CancellationToken applicationLifetime)
    {
        _intake = intake; _preferences = preferences; _lease = lease; _endpoint = endpoint;
        _peerExecutable = Path.GetFullPath(trustedPeerExecutable); _applicationLifetime = applicationLifetime;
        if (!Path.IsPathFullyQualified(trustedPeerExecutable)) throw new ArgumentException("An absolute trusted peer path is required.");
        if (!string.Equals(Path.TrimEndingDirectorySeparator(lease.RuntimeDirectory), Path.TrimEndingDirectorySeparator(endpoint.RuntimeDirectory), StringComparison.OrdinalIgnoreCase)
            || endpoint.PipeName != BrowserBridgeEndpoint.Name(endpoint.RuntimeDirectory, endpoint.Channel, endpoint.PackageModel, endpoint.UserSid, endpoint.InstallationId))
            throw new ArgumentException("Bridge endpoint must match its primary lease context.");
    }
    public async Task StartAsync(CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Browser bridge requires Windows.");
        lock (_admission) { if (_started) throw new InvalidOperationException("Bridge start is single-use."); _started = true; }
        try
        {
            if (!_lease.IsPrimary) throw new InvalidOperationException("Bridge requires the primary instance lease.");
            using var initialize = CancellationTokenSource.CreateLinkedTokenSource(token, _applicationLifetime, _receive.Token);
            initialize.CancelAfter(TimeSpan.FromSeconds(10));
            await _intake.InitializeAsync(initialize.Token).ConfigureAwait(false);
            await _preferences.ResolveDefaultFolderAsync(initialize.Token).ConfigureAwait(false);
            if (!_lease.IsPrimary) throw new InvalidOperationException("Bridge primary lease was released during initialization.");
            // All handles are created before Ready. Keep them alive/reuse them until disposal so the name never disappears.
            for (var index = 0; index < MaximumSessions; index++) _pipes.Add(PrivateBrowserPipe.Create(_endpoint, index == 0));
            lock (_admission)
            {
                initialize.Token.ThrowIfCancellationRequested(); _running = true;
                _workers = _pipes.Select(pipe => Task.Run(() => ReceiveAsync(pipe))).ToArray();
            }
        }
        catch
        {
            Volatile.Write(ref _failure, (int)BrowserBridgeFailure.Initialization);
            foreach (var pipe in _pipes) pipe.Dispose();
            throw;
        }
    }
    // Synchronous barrier is called BEFORE cancellation of application lifetime.
    public void StopAdmission()
    {
        lock (_admission) _running = false;
        _receive.Cancel();
    }
    private async Task ReceiveAsync(NamedPipeServerStream pipe)
    {
        while (!_receive.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(_receive.Token).ConfigureAwait(false);
                if (!PrivateBrowserPipe.VerifyPeer(pipe, _endpoint.UserSid, _peerExecutable))
                { Volatile.Write(ref _failure, (int)BrowserBridgeFailure.PeerDenied); continue; }
                using var session = CancellationTokenSource.CreateLinkedTokenSource(_receive.Token);
                session.CancelAfter(TimeSpan.FromSeconds(15));
                var frames = new FrameConnection(pipe, FrameTransport.PipeLittleEndian);
                for (var count = 0; count < MaximumRequestsPerSession; count++)
                {
                    var body = await frames.ReadAsync(session.Token).ConfigureAwait(false);
                    if (body is null) break;
                    var parsed = ContractJson.ParseRequest(body);
                    if (!parsed.Success)
                    {
                        Volatile.Write(ref _failure, (int)BrowserBridgeFailure.Protocol);
                        if (parsed.CorrelationId is { } correlation)
                            await ReplyAsync(frames, correlation, new(ResultCode.Rejected, Reason: parsed.Error), session.Token).ConfigureAwait(false);
                        break;
                    }
                    Task<ResponseResult> operation;
                    lock (_admission)
                    {
                        if (!_running) break;
                        // Starting the async operation inside the barrier admits it before StopAdmission can return.
                        operation = RouteAsync(parsed.Value!, _applicationLifetime);
                    }
                    var result = await operation.ConfigureAwait(false);
                    await ReplyAsync(frames, parsed.Value!.RequestId, result, session.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_receive.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            { Volatile.Write(ref _failure, (int)BrowserBridgeFailure.Session); }
            catch { Volatile.Write(ref _failure, (int)BrowserBridgeFailure.Session); }
            finally { try { if (pipe.IsConnected) pipe.Disconnect(); } catch (IOException) { } catch (ObjectDisposedException) { } }
        }
    }
    private static ValueTask ReplyAsync(FrameConnection frames, Guid id, ResponseResult result, CancellationToken token) =>
        frames.WriteAsync(ContractJson.Serialize(new ResponseEnvelope(new(1, 0), id, result)), token);
    private async Task<ResponseResult> RouteAsync(RequestEnvelope request, CancellationToken lifetime)
    {
        var installation = request.Payload switch { HelloPayload hello => hello.InstallationId, PublicGetPayload add => add.InstallationId,
            StatusPayload status => status.InstallationId, _ => Guid.Empty };
        if (installation != _endpoint.InstallationId) return new(ResultCode.Rejected, Reason: RejectionReason.InvalidRequest);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            return request.Payload switch
            {
                HelloPayload => new(ResultCode.Ready, AppVersion: "1.0.0", Capabilities: [Capability.AddPublicGet, Capability.GetRequestStatus], MaxMessageBytes: ProtocolLimits.MaximumMessageBytes),
                PublicGetPayload add => Map(await _intake.AcceptAsync(new(new(TrustedCaller, installation, request.RequestId), add.CreatedAt,
                    add.SourceUrl, add.SuggestedFileName, add.Trigger), deadline.Token).ConfigureAwait(false)),
                StatusPayload status => Map(await _intake.GetStatusAsync(new(TrustedCaller, installation, status.TargetRequestId), deadline.Token).ConfigureAwait(false)),
                _ => new(ResultCode.Rejected, Reason: RejectionReason.InvalidRequest)
            };
        }
        catch (OperationCanceledException) { return new(ResultCode.Unknown); }
        catch (DownloadStorageException) { return new(ResultCode.Unavailable); }
        catch { return new(ResultCode.Unavailable); }
    }
    public static ResponseResult Map(ExternalDownloadReceipt receipt) => receipt.Code switch
    {
        ResultCode.Accepted when receipt.JobId is { } job => new(ResultCode.Accepted, JobId: job,
            State: receipt.EngineUncertain ? JobIntakeState.EngineUncertain : receipt.JobStatus switch
            {
                DownloadStatus.Queued => JobIntakeState.Queued, DownloadStatus.Downloading => JobIntakeState.Active,
                DownloadStatus.Paused => JobIntakeState.Paused, DownloadStatus.Completed => JobIntakeState.Completed,
                DownloadStatus.Failed => JobIntakeState.Failed, DownloadStatus.Removed => JobIntakeState.Removed, _ => JobIntakeState.Interrupted
            }, Duplicate: receipt.Duplicate),
        ResultCode.Rejected => new(ResultCode.Rejected, Reason: receipt.Reason ?? RejectionReason.InvalidRequest),
        ResultCode.NotFound or ResultCode.Expired or ResultCode.Unavailable or ResultCode.Unknown => new(receipt.Code),
        _ => new(ResultCode.Unavailable)
    };
    public async Task DrainAsync(CancellationToken token)
    {
        try { await Task.WhenAll(_workers).WaitAsync(token).ConfigureAwait(false); await _intake.DrainAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) { Volatile.Write(ref _failure, (int)BrowserBridgeFailure.DrainDeadline); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        StopAdmission(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await Task.WhenAll(_workers).WaitAsync(timeout.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
        foreach (var pipe in _pipes) pipe.Dispose();
        // Worker cancellation can be delayed by accepted database work; its token source remains usable until all workers exit.
        if (_workers.All(task => task.IsCompleted)) _receive.Dispose();
    }
}
