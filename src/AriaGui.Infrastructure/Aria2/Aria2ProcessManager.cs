using System.ComponentModel;
using System.Diagnostics;
using AriaGui.Core.Downloads;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AriaGui.Application.Engine;
using AriaGui.Application.Downloads;
using AriaGui.Infrastructure.Downloads;

namespace AriaGui.Infrastructure.Aria2;

public sealed partial class Aria2ProcessManager : IManagedAria2Engine, IAsyncDisposable
{
    private readonly Aria2ProcessOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 64 * 1024 };
    private readonly EngineDiagnosticLog _log;
    private Process? _process;
    private FileStream? _lease;
    private Aria2RpcClient? _rpc;
    private EngineInfo? _info;
    private int _expectedExit = 1;
    private bool _wroteOwnership;
    private bool _wroteConfig;
    private string? _sessionFile;
    private readonly IDownloadRepository? _repository;
    private string[] _restoredIds = [];
    private HashSet<string> _knownIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EngineDownload> _observations = new(StringComparer.Ordinal);
    private DateTimeOffset _nextReconcile;
    private int _unknownCount, _concurrency = 3;
    private ContinuityLoopbackGateway? _continuityGateway;
    private ContinuityTransferLedger? _continuityTransfers;
    private SharedRelayRateLimiter? _relayLimiter;
    public bool SupportsFreshRestart => _options.UseContinuityGateway;
    public bool SupportsChecksumVerification => _options.UseContinuityGateway && _options.EnableExpectedChecksumVerification;
    public bool SupportsParallelHttpRanges => SupportsChecksumVerification && _options.ParallelHttpRanges;

    public event EventHandler? UnexpectedExit;
    public int? OwnedProcessId => _process is { HasExited: false } process ? process.Id : null;
    private string ConfigPath => Path.Combine(_options.RuntimeDirectory, "aria2.conf");
    private string OwnershipPath => Path.Combine(_options.RuntimeDirectory, "engine.owner.json");

    public Aria2ProcessManager(Aria2ProcessOptions options, IDownloadRepository? repository = null)
    {
        if (!Path.IsPathFullyQualified(options.ExecutablePath) || !Path.IsPathFullyQualified(options.RuntimeDirectory)
            || options.RuntimeDirectory.IndexOfAny(['\r', '\n']) >= 0
            || options.RpcPort is < 1024 or > 65535 || options.StartupTimeout <= TimeSpan.Zero
            || options.RequestTimeout <= TimeSpan.Zero || options.ShutdownTimeout <= TimeSpan.Zero)
            throw new ArgumentException("Invalid local engine configuration.");
        _options = options;
        _repository = repository;
        _log = new(Path.Combine(options.RuntimeDirectory, "engine.log"));
    }

    public async Task<EngineInfo> StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is { HasExited: false } && _rpc is not null)
            {
                try { return await CheckHealthCoreAsync(cancellationToken).ConfigureAwait(false); }
                catch (EngineException) { await StopOwnedCoreAsync(CancellationToken.None).ConfigureAwait(false); }
            }
            else if (_process is not null) await StopOwnedCoreAsync(CancellationToken.None).ConfigureAwait(false);

            UserRuntimeDirectory.EnsurePrivate(_options.RuntimeDirectory);
            await AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await VerifyBinaryAsync(cancellationToken).ConfigureAwait(false);
                var history = await LoadHistoryAsync(cancellationToken).ConfigureAwait(false);
                if (_options.UseContinuityGateway)
                {
                    _relayLimiter = new();
                    var savedRate = _repository is null ? BandwidthPolicy.Default : await _repository.LoadBandwidthPolicyAsync(cancellationToken).ConfigureAwait(false);
                    savedRate.Validate();
                    var savedSchedule = _repository is null ? BandwidthSchedule.Disabled : await _repository.LoadBandwidthScheduleAsync(cancellationToken).ConfigureAwait(false);
                    savedSchedule.Validate();
                    _relayLimiter.SetRate(savedSchedule.Enabled || savedSchedule.ResetDefaultOnDisable
                        ? savedSchedule.Evaluate(DateTimeOffset.UtcNow).Profile.Download
                        : (savedRate.RequestStatus == OperationStatus.Failed ? savedRate.Confirmed : savedRate.Requested).Download);
                    _continuityGateway = _options.ContinuityTestOrigins is null
                        ? new(_options.RuntimeDirectory, () => OwnedProcessId, enableExperimental: true, upstreamLimit: _relayLimiter)
                        : new(_options.RuntimeDirectory, () => OwnedProcessId, _options.ContinuityTestOrigins, true, _relayLimiter);
                    await _continuityGateway.StartAsync(cancellationToken).ConfigureAwait(false);
                    _continuityTransfers = new(_options.RuntimeDirectory, _continuityGateway);
                    await _continuityTransfers.LoadAsync(cancellationToken, _repository,
                        _options.EnableExpectedChecksumVerification, SupportsParallelHttpRanges).ConfigureAwait(false);
                }
                _observations.Clear(); _nextReconcile = default; _unknownCount = 0;
                _knownIds = _repository is null ? [] : (await _repository.LoadEngineIdsAsync(cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
                if (await TryAdoptAsync(history, cancellationToken).ConfigureAwait(false) is { } adopted) return adopted;
                CheckPortAvailable();
                var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                _log.Secret = secret;
                Directory.CreateDirectory(Path.Combine(_options.RuntimeDirectory, "sessions"));
                _sessionFile = Path.Combine(_options.RuntimeDirectory, "sessions", $"session-{Guid.NewGuid():N}.txt");
                var plan = _repository is null ? new SessionRecoveryPlan([], null, "Ready · local connection verified")
                    : await SessionRecoveryPlanner.PrepareAsync(_options.RuntimeDirectory, history, cancellationToken).ConfigureAwait(false);
                if (_continuityTransfers is not null)
                    plan = await _continuityTransfers.PrepareRestoreAsync(_options.RuntimeDirectory, history, cancellationToken,
                        _options.EnableExpectedChecksumVerification, SupportsParallelHttpRanges).ConfigureAwait(false);
                var policy = _repository is null ? new QueuePolicy() : await _repository.LoadQueuePolicyAsync(cancellationToken).ConfigureAwait(false);
                var bandwidth = _repository is null ? BandwidthPolicy.Default : await _repository.LoadBandwidthPolicyAsync(cancellationToken).ConfigureAwait(false);
                bandwidth.Validate();
                var schedule = _repository is null ? BandwidthSchedule.Disabled : await _repository.LoadBandwidthScheduleAsync(cancellationToken).ConfigureAwait(false);
                schedule.Validate();
                bandwidth = await StageScheduledProfileAsync(bandwidth, schedule, cancellationToken).ConfigureAwait(false);
                var startupProfile = bandwidth.RequestStatus == AriaGui.Core.Downloads.OperationStatus.Failed ? bandwidth.Confirmed : bandwidth.Requested;
                _relayLimiter?.SetRate(startupProfile.Download);
                static string Rate(ByteRateLimit cap) => SpeedLimitPresetPolicy.ToAriaOptionValue(cap.BytesPerSecond);
                var config = string.Join('\n', new[]
                {
                    "enable-rpc=true", "rpc-listen-all=false", "rpc-allow-origin-all=false",
                    $"rpc-listen-port={_options.RpcPort}", $"rpc-secret={secret}", "no-netrc=true",
                    "enable-dht=false", "enable-dht6=false", "enable-peer-exchange=false", "bt-enable-lpd=false",
                    "quiet=true", "show-console-readout=false", "summary-interval=0", "enable-color=false", "file-allocation=none",
                    "auto-save-interval=5", "max-tries=3", "retry-wait=1", "connect-timeout=10", "timeout=30",
                    $"max-concurrent-downloads={policy.RequestedLimit ?? policy.Limit}",
                    $"max-overall-download-limit={Rate(startupProfile.Download)}",
                    $"max-overall-upload-limit={Rate(startupProfile.Upload)}",
                    $"save-session={_sessionFile}", "save-session-interval=5"
                }) + "\n";
                _wroteConfig = true;
                await File.WriteAllTextAsync(ConfigPath, config, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

                var start = new ProcessStartInfo(_options.ExecutablePath)
                {
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _options.RuntimeDirectory,
                    RedirectStandardOutput = false, RedirectStandardError = false
                };
                start.ArgumentList.Add($"--conf-path={ConfigPath}");
                if (plan.InputFile is not null) start.ArgumentList.Add($"--input-file={plan.InputFile}");
                var process = NonInheritingProcessLauncher.Start(_options.ExecutablePath, start.ArgumentList.ToArray(), _options.RuntimeDirectory);
                process.EnableRaisingEvents = true;
                MonitorExit(process);
                _process = process;
                Volatile.Write(ref _expectedExit, 0);
                _log.Write("process", $"Started owned process {process.Id}; waiting for authenticated RPC.");
                _rpc = new(_http, new Uri($"http://127.0.0.1:{_options.RpcPort}/jsonrpc"), secret, _options.RequestTimeout);
                await WriteOwnershipAsync(null, cancellationToken).ConfigureAwait(false);

                using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                startup.CancelAfter(_options.StartupTimeout);
                try
                {
                    while (true)
                    {
                        startup.Token.ThrowIfCancellationRequested();
                        if (process.HasExited) throw new EngineException(EngineFailure.ProcessExited,
                            "aria2 stopped before it became ready. Check the engine diagnostic log.");
                        try
                        {
                            var info = await _rpc.GetInfoAsync(startup.Token).ConfigureAwait(false);
                            if (process.HasExited) throw new EngineException(EngineFailure.ProcessExited, "aria2 stopped during startup.");
                            if (info.Version != Aria2ProcessOptions.BundledVersion)
                                throw new EngineException(EngineFailure.InvalidBinary, "The engine version does not match this app's bundled version.");
                            _info = info with { RecoveryMessage = plan.Message };
                            _restoredIds = plan.Entries.Select(row => row.Attempt.EngineId).ToArray();
                            // Establish a durable checkpoint with all restored entries paused first.
                            await _rpc.SaveSessionAsync(startup.Token).ConfigureAwait(false);
                            await WriteCheckpointAsync(startup.Token).ConfigureAwait(false);
                            await ApplyRestoredJobRatesAsync(_rpc, plan.Entries, startup.Token).ConfigureAwait(false);
                            if (schedule.Enabled && _repository is not null)
                                await _repository.SaveAsync(plan.Entries.Where(row => !DownloadBrowsing.IsTerminal(row.Progress.Status))
                                    .Select(row => row with { PolicyHeld = true }).ToArray(), startup.Token).ConfigureAwait(false);
                            foreach (var entry in plan.Entries.Where(row => !row.DesiredPaused && !schedule.Enabled))
                                if (await _rpc.FindAsync(entry.Attempt.EngineId, startup.Token).ConfigureAwait(false) is { Progress.Status: AriaGui.Core.Downloads.DownloadStatus.Paused })
                                    await _rpc.ExecuteAsync(entry.Attempt.EngineId, AriaGui.Core.Downloads.DownloadAction.Resume, null, startup.Token).ConfigureAwait(false);
                            await _rpc.SaveSessionAsync(startup.Token).ConfigureAwait(false);
                            await WriteOwnershipAsync(_info, startup.Token).ConfigureAwait(false);
                            _log.Write("rpc", $"Authenticated engine ready; version {info.Version}.");
                            return _info;
                        }
                        catch (EngineException ex) when (ex.Failure == EngineFailure.RpcUnavailable) { }
                        await Task.Delay(TimeSpan.FromMilliseconds(100), startup.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { throw new EngineException(EngineFailure.StartupTimeout, "aria2 did not become ready in time. Restart the engine to try again."); }
            }
            catch
            {
                await StopOwnedCoreAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            _log.Write("startup", $"Local startup failure: {ex.GetType().Name}.");
            throw new EngineException(EngineFailure.StorageUnavailable, "The engine could not access its executable or private data folder.");
        }
        finally { _gate.Release(); }
    }

    public async Task<EngineInfo> CheckHealthAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await CheckHealthCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<EngineInfo> CheckHealthCoreAsync(CancellationToken cancellationToken)
    {
        if (_process is not { HasExited: false } || _rpc is null || _info is null)
            throw new EngineException(EngineFailure.ProcessExited, "The owned aria2 engine is not running.");
        var info = await _rpc.GetInfoAsync(cancellationToken).ConfigureAwait(false);
        if (info.SessionId != _info.SessionId || info.Version != _info.Version)
            throw new EngineException(EngineFailure.InvalidResponse, "The engine connection no longer belongs to this session.");
        return _info;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await StopOwnedCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<bool> CanRestartFreshAsync(string engineId, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_continuityTransfers is null || _continuityTransfers.Owns(engineId)) return false;
            var observed = await ConnectedClient().FindAsync(engineId, token).ConfigureAwait(false);
            return observed is null || DownloadBrowsing.IsTerminal(observed.Progress.Status);
        }
        finally { _gate.Release(); }
    }

    public async Task<EngineDownload> AddDownloadAsync(EngineDownloadRequest request, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var rpc = ConnectedClient();
            if (request.HttpConnections is not (1 or 2 or 4) || request.HttpConnections > 1 &&
                (!SupportsParallelHttpRanges || _repository is null || request.ExpectedChecksum is null || _continuityTransfers is null))
                throw new EngineException(EngineFailure.RequestRejected, "Parallel HTTP requires guarded, durable SHA-256 verification and explicit opt-in.");
            if (request.ExpectedChecksum is not null && (_continuityTransfers is null || !_options.EnableExpectedChecksumVerification))
                throw new EngineException(EngineFailure.RequestRejected, "Expected SHA-256 requires guarded staging.");
            if (request.ExpectedChecksum is not null && _repository is null)
                throw new EngineException(EngineFailure.RequestRejected, "Expected SHA-256 requires durable job intent.");
            if (_repository is not null && request.ContinuityJobId is Guid expectedJob)
            {
                var savedSpec = await _repository.GetAsync(expectedJob, token).ConfigureAwait(false);
                if (savedSpec is null || savedSpec.Attempt.Id != request.ContinuityAttemptId ||
                    savedSpec.Job.ExpectedChecksum != request.ExpectedChecksum || savedSpec.Attempt.HttpConnections != request.HttpConnections)
                    throw new EngineException(EngineFailure.RequestRejected, "Saved checksum specification changed.");
            }
            if (request.FreshRestart && (_continuityTransfers is null || request.LegacyParentEngineId is null || _continuityTransfers.Owns(request.LegacyParentEngineId)))
                throw new EngineException(EngineFailure.RequestRejected, "Use Recover for a protected attempt; a legacy fresh restart requires a protected engine session.");
            if (_continuityTransfers is not null)
            {
                var legacyFresh = false;
                if (request.LegacyParentEngineId is { } parentGid && !_continuityTransfers.Owns(parentGid))
                {
                    var current = _repository is null || request.ContinuityJobId is not Guid job ? null : await _repository.GetAsync(job, token).ConfigureAwait(false);
                    var parent = _repository is null ? null : await _repository.GetAttemptAsync(parentGid, token).ConfigureAwait(false);
                    var observed = await rpc.FindAsync(parentGid, token).ConfigureAwait(false);
                    legacyFresh = current is not null && parent is not null && current.Attempt.Id == request.ContinuityAttemptId
                        && current.Attempt.EngineId == request.EngineId && current.Attempt.ParentAttemptId == parent.Attempt.Id
                        && parent.Job.Id == current.Job.Id && (parent.Progress.Status is DownloadStatus.Failed or DownloadStatus.Removed || request.FreshRestart && parent.Progress.Status == DownloadStatus.Interrupted)
                        && current.PendingOperation is { Action: DownloadAction.Retry, Status: OperationStatus.Pending }
                        && current.Job.Destination == Path.GetFullPath(Path.Combine(request.Directory, request.FileName))
                        && (current.ExactSourceUrl ?? current.Job.Source.AbsoluteUri) == (request.ExactSourceUrl ?? request.Source.AbsoluteUri)
                        && (observed is null || DownloadBrowsing.IsTerminal(observed.Progress.Status));
                    if (!legacyFresh) throw new EngineException(EngineFailure.RequestRejected, "Legacy restart lacks reconciled durable retry provenance.");
                    request = request with { RequirePublicOrigin = current!.ExactSourceUrl is not null };
                }
                request = await _continuityTransfers.PrepareAsync(request, token, legacyFresh).ConfigureAwait(false);
            }
            if (_repository is not null && request.ContinuityJobId is Guid jobId)
            {
                var saved = await _repository.GetAsync(jobId, token).ConfigureAwait(false);
                if (saved is null || saved.Attempt.EngineId != request.EngineId || saved.Attempt.Id != request.ContinuityAttemptId)
                    throw new EngineException(EngineFailure.RequestRejected, "Saved job ownership changed before adding a transfer.");
                var policy = await _repository.LoadJobBandwidthPolicyAsync(jobId, token).ConfigureAwait(false);
                policy.Validate();
                if (policy.RequestedRevision > 0)
                    request = request with { JobDownloadLimit = policy.RequestStatus == OperationStatus.Failed ? policy.Confirmed : policy.Requested };
            }
            var download = await rpc.AddAsync(request, token).ConfigureAwait(false);
            if (_continuityTransfers is not null) download = await ObserveContinuityAsync(download, token).ConfigureAwait(false);
            _knownIds.Add(request.EngineId);
            // A checkpoint failure leaves the durable intent uncertain; the known GID is reconciled.
            await rpc.SaveSessionAsync(token).ConfigureAwait(false);
            return download;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<EngineDownload>> GetDownloadsAsync(IReadOnlyList<string> gids, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var rpc = ConnectedClient();
            var result = new List<EngineDownload>();
            foreach (var gid in gids)
                if (await rpc.FindAsync(gid, token).ConfigureAwait(false) is { } download)
                    result.Add(_continuityTransfers is null ? download : await ObserveContinuityAsync(download, token).ConfigureAwait(false));
            return result;
        }
        finally { _gate.Release(); }
    }

    private Aria2RpcClient ConnectedClient() => _process is { HasExited: false } && _info is not null && _rpc is not null
        ? _rpc : throw new EngineException(EngineFailure.ProcessExited, "The owned aria2 engine is not ready.");

    public async Task<EngineObservation> ObserveDownloadsAsync(IReadOnlyList<string> gids, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var rpc = ConnectedClient();
            var requested = gids.ToHashSet(StringComparer.Ordinal);
            if (DateTimeOffset.UtcNow >= _nextReconcile || gids.Any(gid => !_observations.ContainsKey(gid)))
            {
                await CheckHealthCoreAsync(token).ConfigureAwait(false);
                var waiting = (await rpc.GetWaitingAsync(token).ConfigureAwait(false)).Select((gid, index) => (gid, index)).ToDictionary(row => row.gid, row => row.index);
                var found = await rpc.FindManyAsync(gids, token).ConfigureAwait(false);
                _observations.Clear();
                foreach (var download in found) _observations[download.EngineId] = download with
                    { QueuePosition = waiting.TryGetValue(download.EngineId, out var position) ? position : null };
                _concurrency = await rpc.GetConcurrencyAsync(token).ConfigureAwait(false);
                _unknownCount = _repository is null ? 0 : (await rpc.GetInventoryAsync(token).ConfigureAwait(false)).Count(gid => !_knownIds.Contains(gid));
                _nextReconcile = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
            }
            else
            {
                var active = await rpc.GetActiveAsync(token).ConfigureAwait(false);
                var activeIds = active.Select(row => row.EngineId).ToHashSet(StringComparer.Ordinal);
                // A disappearing active item needs an immediate terminal/missing-ID check.
                var ended = _observations.Values.Where(row => requested.Contains(row.EngineId)
                    && row.Progress.Status == AriaGui.Core.Downloads.DownloadStatus.Downloading && !activeIds.Contains(row.EngineId)).Select(row => row.EngineId).ToArray();
                foreach (var gid in ended) _observations.Remove(gid);
                foreach (var row in await rpc.FindManyAsync(ended, token).ConfigureAwait(false)) _observations[row.EngineId] = row;
                foreach (var row in active.Where(row => requested.Contains(row.EngineId))) _observations[row.EngineId] = row;
                foreach (var gid in _observations.Keys.Where(gid => !requested.Contains(gid)).ToArray()) _observations.Remove(gid);
            }
            var rows = new List<EngineDownload>();
            foreach (var gid in gids.Where(_observations.ContainsKey))
                rows.Add(_continuityTransfers is null ? _observations[gid] : await ObserveContinuityAsync(_observations[gid], token).ConfigureAwait(false));
            if (_continuityTransfers is not null && _repository is not null)
                foreach (var gid in gids.Where(gid => !rows.Any(row => row.EngineId == gid) && _knownIds.Contains(gid)))
                    if (await _repository.GetAttemptAsync(gid, token).ConfigureAwait(false) is { } entry &&
                        await _continuityTransfers.PublishedReceiptAsync(gid, entry, token).ConfigureAwait(false) is { } receipt)
                        rows.Add(receipt);
            return new(_info!.SessionId, rows, _concurrency, _restoredIds, _unknownCount);
        }
        finally { _gate.Release(); }
    }

    public async Task<EngineCommandResult> ExecuteDownloadAsync(string gid, AriaGui.Core.Downloads.DownloadAction action, int? position, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var rpc = ConnectedClient();
            var info = await CheckHealthCoreAsync(token).ConfigureAwait(false);
            if (_continuityTransfers is not null && action == AriaGui.Core.Downloads.DownloadAction.Resume && !_continuityTransfers.CanResume(gid))
                throw new EngineException(EngineFailure.RequestRejected, "Legacy or published transfer cannot resume without active continuity ownership.");
            await rpc.ExecuteAsync(gid, action, position, token).ConfigureAwait(false);
            _nextReconcile = default;
            await rpc.SaveSessionAsync(token).ConfigureAwait(false);
            var download = await rpc.FindAsync(gid, token).ConfigureAwait(false);
            if (download is not null && _continuityTransfers is not null) download = await ObserveContinuityAsync(download, token).ConfigureAwait(false);
            if (download is not null)
            {
                var index = (await rpc.GetWaitingAsync(token).ConfigureAwait(false)).ToList().IndexOf(gid);
                download = download with { QueuePosition = index < 0 ? null : index };
            }
            return new(info.SessionId, download);
        }
        finally { _gate.Release(); }
    }

    public async Task SetConcurrencyAsync(int limit, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var rpc = ConnectedClient();
            await CheckHealthCoreAsync(token).ConfigureAwait(false);
            await rpc.SetConcurrencyAsync(limit, token).ConfigureAwait(false);
            _nextReconcile = default;
            if (await rpc.GetConcurrencyAsync(token).ConfigureAwait(false) != limit)
                throw new EngineException(EngineFailure.InvalidResponse, "The queue setting was not confirmed.");
        }
        finally { _gate.Release(); }
    }

    public async Task<BandwidthObservation> ApplyBandwidthAsync(BandwidthProfile profile, CancellationToken token)
    {
        profile.Validate();
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var rpc = ConnectedClient();
            var info = await CheckHealthCoreAsync(token).ConfigureAwait(false);
            _relayLimiter?.SetRate(profile.Download);
            await rpc.SetBandwidthAsync(profile, token).ConfigureAwait(false);
            var (download, upload) = await rpc.GetBandwidthAsync(token).ConfigureAwait(false);
            _nextReconcile = default;
            return new(info.SessionId, download, upload);
        }
        finally { _gate.Release(); }
    }

    public async Task<BandwidthObservation> ObserveBandwidthAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var rpc = ConnectedClient();
            var info = await CheckHealthCoreAsync(token).ConfigureAwait(false);
            var (download, upload) = await rpc.GetBandwidthAsync(token).ConfigureAwait(false);
            return new(info.SessionId, download, upload);
        }
        finally { _gate.Release(); }
    }

    private async Task<DownloadEntry> RequireOwnedJobAsync(Guid jobId, string gid, Aria2RpcClient rpc, CancellationToken token)
    {
        var entry = _repository is null ? null : await _repository.GetAsync(jobId, token).ConfigureAwait(false);
        if (entry is null || entry.Attempt.EngineId != gid || !_knownIds.Contains(gid)
            || DownloadBrowsing.IsTerminal(entry.Progress.Status))
            throw new EngineException(EngineFailure.RequestRejected, "The job is not an active owned transfer.");
        if (_continuityTransfers is not null)
        {
            if (!_continuityTransfers.Owns(gid, entry.Job.Id, entry.Attempt.Id))
                throw new EngineException(EngineFailure.RequestRejected, "The transfer lacks guarded ownership.");
        }
        else if (!await rpc.MatchesTransferOwnershipAsync(gid, entry.ExactSourceUrl ?? entry.Job.Source.AbsoluteUri,
                     entry.Job.Destination, token).ConfigureAwait(false))
            throw new EngineException(EngineFailure.RequestRejected, "The transfer identity changed.");
        return entry;
    }

    public async Task<JobBandwidthObservation> ApplyJobBandwidthAsync(Guid jobId, string gid, ByteRateLimit cap, CancellationToken token)
    {
        cap.Validate();
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var rpc = ConnectedClient();
            var info = await CheckHealthCoreAsync(token).ConfigureAwait(false);
            await RequireOwnedJobAsync(jobId, gid, rpc, token).ConfigureAwait(false);
            await rpc.SetJobBandwidthAsync(gid, cap, token).ConfigureAwait(false);
            var effective = await rpc.GetJobBandwidthAsync(gid, token).ConfigureAwait(false);
            return new(info.SessionId, gid, effective);
        }
        finally { _gate.Release(); }
    }

    public async Task<JobBandwidthObservation> ObserveJobBandwidthAsync(Guid jobId, string gid, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var rpc = ConnectedClient();
            var info = await CheckHealthCoreAsync(token).ConfigureAwait(false);
            await RequireOwnedJobAsync(jobId, gid, rpc, token).ConfigureAwait(false);
            return new(info.SessionId, gid, await rpc.GetJobBandwidthAsync(gid, token).ConfigureAwait(false));
        }
        finally { _gate.Release(); }
    }

    private async Task ApplyRestoredJobRatesAsync(Aria2RpcClient rpc, IReadOnlyList<DownloadEntry> entries, CancellationToken token)
    {
        if (_repository is null) return;
        foreach (var entry in entries)
        {
            var policy = await _repository.LoadJobBandwidthPolicyAsync(entry.Job.Id, token).ConfigureAwait(false);
            policy.Validate();
            if (policy.RequestedRevision == 0) continue;
            var transfer = await rpc.FindAsync(entry.Attempt.EngineId, token).ConfigureAwait(false);
            if (transfer is null || DownloadBrowsing.IsTerminal(transfer.Progress.Status)) continue;
            var desired = policy.RequestStatus == OperationStatus.Failed ? policy.Confirmed : policy.Requested;
            var observed = await rpc.GetJobBandwidthAsync(entry.Attempt.EngineId, token).ConfigureAwait(false);
            if (observed != desired) await rpc.SetJobBandwidthAsync(entry.Attempt.EngineId, desired, token).ConfigureAwait(false);
            if (await rpc.GetJobBandwidthAsync(entry.Attempt.EngineId, token).ConfigureAwait(false) != desired)
                throw new EngineException(EngineFailure.InvalidResponse, "A restored job rate was not confirmed.");
        }
    }

    private async Task<BandwidthPolicy> StageScheduledProfileAsync(BandwidthPolicy current, BandwidthSchedule schedule, CancellationToken token)
    {
        if ((!schedule.Enabled && !schedule.ResetDefaultOnDisable) || _repository is null) return current;
        var desired = schedule.Evaluate(DateTimeOffset.UtcNow).Profile;
        var effectiveIntent = current.RequestStatus == OperationStatus.Failed ? current.Confirmed : current.Requested;
        if (desired == effectiveIntent && !(schedule.ResetDefaultOnDisable && current.RequestedRevision == 0)) return current;
        var staged = current with { RequestedRevision = checked(current.RequestedRevision + 1),
            Requested = desired, RequestStatus = OperationStatus.Pending };
        await _repository.SaveBandwidthPolicyAsync(staged, token).ConfigureAwait(false);
        return staged;
    }

    private async Task StopOwnedCoreAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _expectedExit, 1);
        var process = _process;
        if (process is not null)
        {
            // Process.Start can fail before the Process object has an associated OS process.
            bool running;
            try { running = !process.HasExited; }
            catch (InvalidOperationException) { running = false; }
            if (running)
            {
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                stop.CancelAfter(_options.ShutdownTimeout);
                if (_rpc is not null)
                {
                    try { await _rpc.SaveSessionAsync(stop.Token).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is EngineException or OperationCanceledException)
                    { _log.Write("shutdown", "Session checkpoint unavailable; proceeding with shutdown."); }
                    try { await _rpc.ShutdownAsync(stop.Token).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is EngineException or OperationCanceledException)
                    { _log.Write("shutdown", "Graceful RPC shutdown unavailable."); }
                }
                try { await process.WaitForExitAsync(stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited)
                    {
                        _log.Write("shutdown", "Graceful shutdown deadline exceeded; terminating only the owned process.");
                        try { process.Kill(); }
                        catch (InvalidOperationException) { }
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                    }
                }
                _log.Write("shutdown", $"Owned engine exited with code {process.ExitCode}.");
            }
            process.Dispose();
        }
        _process = null;
        _rpc = null;
        _info = null;
        if (_continuityGateway is not null) { await _continuityGateway.DisposeAsync().ConfigureAwait(false); _continuityGateway = null; }
        _relayLimiter = null;
        _continuityTransfers?.Dispose(); _continuityTransfers = null;
        if (_wroteOwnership)
        {
            try { File.Delete(OwnershipPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { _log.Write("shutdown", "Could not remove expired runtime metadata."); }
        }
        _wroteOwnership = false;
        if (_wroteConfig)
        {
            try { File.Delete(ConfigPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { _log.Write("shutdown", "Could not remove expired engine configuration."); }
        }
        _wroteConfig = false;
        _lease?.Dispose();
        _lease = null;
    }

    private async Task AcquireLeaseAsync(CancellationToken token)
    {
        // Process exit can briefly precede final kernel handle cleanup. Retry only sharing
        // violations, with a one-second bound; never remove or bypass another owner's lock.
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                _lease = new FileStream(Path.Combine(_options.RuntimeDirectory, "engine.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                return;
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
            {
                if (attempt >= 5) throw new EngineException(EngineFailure.AlreadyRunning, "Another app already owns this engine data folder.");
                await Task.Delay(200, token).ConfigureAwait(false);
            }
        }
    }

    private async Task VerifyBinaryAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_options.ExecutablePath))
            throw new EngineException(EngineFailure.MissingBinary, "The bundled aria2 engine is missing. Rebuild or reinstall the app.");
        await using var file = File.OpenRead(_options.ExecutablePath);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
        if (!string.Equals(hash, _options.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new EngineException(EngineFailure.InvalidBinary, "The bundled aria2 engine failed its integrity check. Rebuild or reinstall the app.");
    }

    private void CheckPortAvailable()
    {
        try
        {
            using var ipv4 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
            ipv4.Bind(new IPEndPoint(IPAddress.Loopback, _options.RpcPort));
            if (Socket.OSSupportsIPv6)
            {
                using var ipv6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp)
                    { ExclusiveAddressUse = true, DualMode = false };
                ipv6.Bind(new IPEndPoint(IPAddress.IPv6Loopback, _options.RpcPort));
            }
        }
        catch (SocketException)
        { throw new EngineException(EngineFailure.PortInUse, "The engine's local port is unavailable. Close the conflicting app and restart the engine."); }
    }

    private async Task WriteOwnershipAsync(EngineInfo? info, CancellationToken cancellationToken)
    {
        var owner = new EngineOwnership(_process!.Id, _process.StartTime.ToUniversalTime(),
            Path.GetFullPath(_options.ExecutablePath), _options.RpcPort, info?.SessionId, _sessionFile, Environment.ProcessId,
            Process.GetCurrentProcess().StartTime.ToUniversalTime(), _options.UseContinuityGateway);
        // A cancelled write must not leave a half-written ownership record for the next launch.
        var temporaryPath = OwnershipPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(file, owner, cancellationToken: cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false); file.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, OwnershipPath, overwrite: true);
            _wroteOwnership = true;
        }
        finally { File.Delete(temporaryPath); }
    }

    private async Task<IReadOnlyList<DownloadEntry>> LoadHistoryAsync(CancellationToken token)
    {
        if (_repository is null) return [];
        await _repository.InitializeAsync(token).ConfigureAwait(false);
        return await _repository.LoadCurrentAsync(token).ConfigureAwait(false);
    }
    private void MonitorExit(Process process) => process.Exited += (_, _) =>
    {
        if (Volatile.Read(ref _expectedExit) != 0 || !ReferenceEquals(_process, process)) return;
        _log.Write("process", "Owned engine exited unexpectedly.");
        UnexpectedExit?.Invoke(this, EventArgs.Empty);
    };
    private async Task WriteCheckpointAsync(CancellationToken token)
    {
        var path = Path.Combine(_options.RuntimeDirectory, "engine.checkpoint.json");
        var temporary = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new SessionCheckpoint(_sessionFile!)), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
    }

    private async Task<EngineInfo?> TryAdoptAsync(IReadOnlyList<DownloadEntry> history, CancellationToken token)
    {
        if (!File.Exists(OwnershipPath)) return null;
        EngineOwnership? owner;
        try
        {
            if (new FileInfo(OwnershipPath).Length > 16384) throw new JsonException();
            owner = JsonSerializer.Deserialize<EngineOwnership>(await File.ReadAllTextAsync(OwnershipPath, token).ConfigureAwait(false));
        }
        catch (JsonException) { throw AdoptionRefused("Existing engine ownership information is unreadable. Keep it for diagnosis."); }
        if (owner is null) throw AdoptionRefused("Existing engine ownership information is empty.");
        Process existing;
        try { existing = Process.GetProcessById(owner.ProcessId); }
        catch (ArgumentException) { return null; }
        try
        {
            if (existing.HasExited || existing.StartTime.ToUniversalTime() != owner.StartedAtUtc) { existing.Dispose(); return null; }
            if (owner.Guarded != _options.UseContinuityGateway) throw AdoptionRefused(!owner.Guarded && _options.UseContinuityGateway
                ? "An older direct-download engine is still running. Stop it normally before protected startup; its partial files and process were preserved."
                : "Surviving engine transport ownership does not match. Its files and process were preserved.");
            if (_repository is null || !string.Equals(Path.GetFullPath(owner.ExecutablePath), Path.GetFullPath(_options.ExecutablePath), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(existing.MainModule?.FileName, Path.GetFullPath(_options.ExecutablePath), StringComparison.OrdinalIgnoreCase)
                || owner.RpcPort != _options.RpcPort || string.IsNullOrWhiteSpace(owner.SessionId)
                || owner.SessionFile is null || !SessionRecoveryPlanner.IsSessionPath(_options.RuntimeDirectory, owner.SessionFile))
                throw AdoptionRefused("A surviving process could not be verified as this app's engine. It was not contacted or stopped.");
            try
            {
                using var previousApp = Process.GetProcessById(owner.AppProcessId);
                if (!previousApp.HasExited && (owner.AppStartedAtUtc is null || previousApp.StartTime.ToUniversalTime() == owner.AppStartedAtUtc))
                    throw AdoptionRefused("The previous app still owns this engine. Open that instance instead.");
            }
            catch (ArgumentException) { }
            var settings = (await File.ReadAllLinesAsync(ConfigPath, token).ConfigureAwait(false)).Where(line => line.Contains('='))
                .ToDictionary(line => line[..line.IndexOf('=')], line => line[(line.IndexOf('=') + 1)..], StringComparer.Ordinal);
            if (!settings.TryGetValue("rpc-secret", out var secret) || secret.Length != 64 || !secret.All(Uri.IsHexDigit)
                || !settings.TryGetValue("rpc-listen-all", out var all) || all != "false"
                || !settings.TryGetValue("rpc-listen-port", out var port) || port != _options.RpcPort.ToString(System.Globalization.CultureInfo.InvariantCulture)
                || settings.ContainsKey("stop-with-process") || !settings.TryGetValue("save-session", out var file) || file != owner.SessionFile)
                throw AdoptionRefused("The surviving engine configuration cannot be verified safely. It was preserved.");
            var rpc = new Aria2RpcClient(_http, new($"http://127.0.0.1:{owner.RpcPort}/jsonrpc"), secret, _options.RequestTimeout);
            var info = await rpc.GetInfoAsync(token).ConfigureAwait(false);
            if (info.SessionId != owner.SessionId || info.Version != Aria2ProcessOptions.BundledVersion)
                throw AdoptionRefused("The surviving engine's authenticated session does not match its ownership record.");
            if (_continuityTransfers is not null && !await _continuityTransfers.VerifySurvivorAsync(rpc, history, token, _repository).ConfigureAwait(false))
                throw AdoptionRefused($"Surviving guarded transfer ownership could not be verified ({rpc.OwnershipFailure}). Its files and process were preserved.");
            // Adopt only after every identity/authentication check succeeds.
            _log.Secret = secret; _process = existing; _rpc = rpc; _sessionFile = owner.SessionFile;
            _wroteConfig = true; _wroteOwnership = true;
            Volatile.Write(ref _expectedExit, 0); existing.EnableRaisingEvents = true; MonitorExit(existing);
            var queuePolicy = await _repository!.LoadQueuePolicyAsync(token).ConfigureAwait(false);
            if (queuePolicy.RequestedLimit is int requestedLimit && await rpc.GetConcurrencyAsync(token).ConfigureAwait(false) != requestedLimit)
                await rpc.SetConcurrencyAsync(requestedLimit, token).ConfigureAwait(false);
            var bandwidth = await _repository.LoadBandwidthPolicyAsync(token).ConfigureAwait(false);
            bandwidth.Validate();
            var schedule = await _repository.LoadBandwidthScheduleAsync(token).ConfigureAwait(false);
            schedule.Validate();
            bandwidth = await StageScheduledProfileAsync(bandwidth, schedule, token).ConfigureAwait(false);
            var profile = bandwidth.RequestStatus == AriaGui.Core.Downloads.OperationStatus.Failed ? bandwidth.Confirmed : bandwidth.Requested;
            _relayLimiter?.SetRate(profile.Download);
            if (bandwidth.RequestedRevision > 0)
            {
                var (download, upload) = await rpc.GetBandwidthAsync(token).ConfigureAwait(false);
                if (download != profile.Download || upload != profile.Upload)
                    await rpc.SetBandwidthAsync(profile, token).ConfigureAwait(false);
            }
            await ApplyRestoredJobRatesAsync(rpc, history.Where(row => !DownloadBrowsing.IsTerminal(row.Progress.Status)).ToArray(), token)
                .ConfigureAwait(false);
            if (schedule.Enabled)
                await _repository.SaveAsync(history.Where(row => !DownloadBrowsing.IsTerminal(row.Progress.Status))
                    .Select(row => row with { PolicyHeld = true }).ToArray(), token).ConfigureAwait(false);
            foreach (var entry in history.Where(row => row.Progress.Status is not
                (AriaGui.Core.Downloads.DownloadStatus.Completed or AriaGui.Core.Downloads.DownloadStatus.Failed or AriaGui.Core.Downloads.DownloadStatus.Removed)))
            {
                var observed = await rpc.FindAsync(entry.Attempt.EngineId, token).ConfigureAwait(false);
                if (observed is null) continue;
                var status = observed.Progress.Status;
                if (entry.DesiredRemoved && status is AriaGui.Core.Downloads.DownloadStatus.Downloading or AriaGui.Core.Downloads.DownloadStatus.Queued or AriaGui.Core.Downloads.DownloadStatus.Paused)
                    await rpc.ExecuteAsync(entry.Attempt.EngineId, AriaGui.Core.Downloads.DownloadAction.Remove, null, token).ConfigureAwait(false);
                else if ((entry.DesiredPaused || schedule.Enabled) && status is AriaGui.Core.Downloads.DownloadStatus.Downloading or AriaGui.Core.Downloads.DownloadStatus.Queued)
                    await rpc.ExecuteAsync(entry.Attempt.EngineId, AriaGui.Core.Downloads.DownloadAction.Pause, null, token).ConfigureAwait(false);
                else if (!entry.DesiredPaused && !schedule.Enabled && entry.PendingOperation?.Action == AriaGui.Core.Downloads.DownloadAction.Resume && status == AriaGui.Core.Downloads.DownloadStatus.Paused)
                    await rpc.ExecuteAsync(entry.Attempt.EngineId, AriaGui.Core.Downloads.DownloadAction.Resume, null, token).ConfigureAwait(false);
            }
            _info = info with { RecoveryMessage = "Reconnected to the verified surviving engine; checking saved intents and history." };
            await rpc.SaveSessionAsync(token).ConfigureAwait(false);
            await WriteCheckpointAsync(token).ConfigureAwait(false);
            await WriteOwnershipAsync(_info, token).ConfigureAwait(false);
            _log.Write("recovery", "Adopted verified surviving engine.");
            return _info;
        }
        catch (InvalidOperationException) when (_process is null) { existing.Dispose(); return null; }
        catch (Win32Exception)
        { if (_process is null) existing.Dispose(); throw AdoptionRefused("A previous engine could not be identified safely. No new engine was started."); }
        catch { if (_process is null) existing.Dispose(); throw; }
    }

    private static EngineException AdoptionRefused(string message) => new(EngineFailure.AlreadyRunning, message);

    public async Task CheckpointAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { await ConnectedClient().SaveSessionAsync(token).ConfigureAwait(false); await WriteCheckpointAsync(token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    public async Task<EngineDownload> RecoverDownloadAsync(DownloadEntry entry, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var rpc = ConnectedClient();
            await CheckHealthCoreAsync(token).ConfigureAwait(false);
            if (await rpc.FindAsync(entry.Attempt.EngineId, token).ConfigureAwait(false) is { } present)
            {
                if (_continuityTransfers is not null && !_continuityTransfers.Owns(entry.Attempt.EngineId))
                    throw new EngineException(EngineFailure.RequestRejected, "Legacy recovery cannot bypass continuity ownership.");
                if (_continuityTransfers is not null && !_continuityTransfers.CanResume(entry.Attempt.EngineId) && present.Progress.Status != DownloadStatus.Completed)
                    throw new EngineException(EngineFailure.RequestRejected, "Published transfer cannot become active during recovery.");
                var status = present.Progress.Status;
                if (entry.DesiredRemoved && status is AriaGui.Core.Downloads.DownloadStatus.Downloading or AriaGui.Core.Downloads.DownloadStatus.Queued or AriaGui.Core.Downloads.DownloadStatus.Paused)
                    await rpc.ExecuteAsync(entry.Attempt.EngineId, AriaGui.Core.Downloads.DownloadAction.Remove, null, token).ConfigureAwait(false);
                else if ((entry.DesiredPaused || entry.PolicyHeld) && status is AriaGui.Core.Downloads.DownloadStatus.Downloading or AriaGui.Core.Downloads.DownloadStatus.Queued)
                    await rpc.ExecuteAsync(entry.Attempt.EngineId, AriaGui.Core.Downloads.DownloadAction.Pause, null, token).ConfigureAwait(false);
                else if (!entry.DesiredPaused && !entry.PolicyHeld && status == AriaGui.Core.Downloads.DownloadStatus.Paused)
                    await rpc.ExecuteAsync(entry.Attempt.EngineId, AriaGui.Core.Downloads.DownloadAction.Resume, null, token).ConfigureAwait(false);
                if (entry.PendingOperation?.QueuePosition is int position && status == AriaGui.Core.Downloads.DownloadStatus.Queued)
                    await rpc.ExecuteAsync(entry.Attempt.EngineId, AriaGui.Core.Downloads.DownloadAction.Move, position, token).ConfigureAwait(false);
                await rpc.SaveSessionAsync(token).ConfigureAwait(false);
                var recovered = await rpc.FindAsync(entry.Attempt.EngineId, token).ConfigureAwait(false) ?? throw new EngineException(EngineFailure.TransferNotFound, "Transfer disappeared during recovery; no duplicate Add was sent.");
                return _continuityTransfers is null ? recovered : await ObserveContinuityAsync(recovered, token).ConfigureAwait(false);
            }
            if (entry.DesiredRemoved)
                return new(entry.Attempt.EngineId, new(AriaGui.Core.Downloads.DownloadStatus.Removed, entry.Progress.DownloadedBytes, entry.Progress.TotalBytes, 0));
            var target = await new AriaGui.Infrastructure.Downloads.FileDownloadDestination().ValidateRetryAsync(entry.Job.Source,
                Path.GetDirectoryName(entry.Job.Destination)!, entry.File.FileName, token).ConfigureAwait(false);
            if (entry.Job.ExpectedChecksum is not null && (!SupportsChecksumVerification || _repository is null))
                throw new EngineException(EngineFailure.RequestRejected, "Required SHA-256 verification is disabled; attempt preserved for review.");
            if (entry.Attempt.HttpConnections > 1 && !SupportsParallelHttpRanges)
                throw new EngineException(EngineFailure.RequestRejected, "Parallel HTTP is disabled; private stage preserved for review.");
            var recoveryRequest = new EngineDownloadRequest(entry.Attempt.EngineId, entry.Job.Source, Directory: target.Directory, FileName: target.FileName,
                ContinueExisting: target.ContinueExisting, StartPaused: entry.DesiredPaused || entry.PolicyHeld, ExactSourceUrl: entry.ExactSourceUrl,
                ContinuityJobId: entry.Job.Id, ContinuityAttemptId: entry.Attempt.Id,
                RequirePublicOrigin: entry.ExactSourceUrl is not null, ExpectedChecksum: entry.Job.ExpectedChecksum,
                HttpConnections: entry.Attempt.HttpConnections);
            if (_repository is not null)
            {
                var jobRate = await _repository.LoadJobBandwidthPolicyAsync(entry.Job.Id, token).ConfigureAwait(false);
                jobRate.Validate();
                if (jobRate.RequestedRevision > 0)
                    recoveryRequest = recoveryRequest with { JobDownloadLimit = jobRate.RequestStatus == OperationStatus.Failed
                        ? jobRate.Confirmed : jobRate.Requested };
            }
            if (_continuityTransfers is not null) recoveryRequest = await _continuityTransfers.PrepareAsync(recoveryRequest, token).ConfigureAwait(false);
            var result = await rpc.AddAsync(recoveryRequest, token).ConfigureAwait(false);
            _knownIds.Add(entry.Attempt.EngineId);
            await rpc.SaveSessionAsync(token).ConfigureAwait(false);
            return _continuityTransfers is null ? result : await ObserveContinuityAsync(result, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<EngineDownload> ObserveContinuityAsync(EngineDownload download, CancellationToken token)
    {
        try { return await _continuityTransfers!.ObserveAsync(download, token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keep the durable journal and retained/staged files for explicit recovery.
            // A destination collision must never become a false Completed observation.
            return download with
            {
                Progress = new(AriaGui.Core.Downloads.DownloadStatus.Failed, download.Progress.DownloadedBytes, download.Progress.TotalBytes, 0),
                ErrorMessage = "Protected file publication is unavailable; retained files were preserved."
            };
        }
    }
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _http.Dispose();
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed record EngineOwnership(int ProcessId, DateTime StartedAtUtc, string ExecutablePath,
        int RpcPort, string? SessionId, string? SessionFile, int AppProcessId, DateTime? AppStartedAtUtc = null, bool Guarded = false);
}
