using System.Security.Cryptography;
using System.Collections.Concurrent;
using AriaGui.Application.Engine;
using AriaGui.Core.Downloads;

namespace AriaGui.Application.Downloads;

public sealed partial class DownloadCoordinator(IAria2Client engine, DownloadRuntimeStore store, TimeProvider clock,
    IDownloadRepository repository, IDownloadDestination destination)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<Guid> _inFlight = [];
    private readonly Dictionary<Guid, DateTimeOffset> _checkpoints = [];
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _jobGates = new();
    private readonly SemaphoreSlim _policyGate = new(1, 1);
    private QueuePolicy _policy = new();
    private long _epoch, _issuedPoll, _appliedPoll;
    private string? _generation, _configuredGeneration;
    private bool _initialized;
    private int _running;

    public async Task InitializeAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            await repository.InitializeAsync(token).ConfigureAwait(false);
            var history = await repository.LoadCurrentAsync(token).ConfigureAwait(false);
            _policy = await repository.LoadQueuePolicyAsync(token).ConfigureAwait(false);
            store.Publish(history, EngineConnectionState.Starting, policy: _policy);
            _initialized = true;
        }
        finally { _gate.Release(); }
    }

    public async Task AddAsync(string url, bool unknownSize = false, CancellationToken cancellationToken = default,
        string directory = "", string? fileName = null)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var source)
            || (source.Scheme != Uri.UriSchemeHttp && source.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(source.Host)) throw new ArgumentException("Enter a complete HTTP or HTTPS URL.");
        if (!string.IsNullOrEmpty(source.UserInfo)) throw new ArgumentException("Use a URL without embedded credentials.");
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (engine.SupportsFreshRestart) await destination.ValidateProtectedAsync(directory, cancellationToken).ConfigureAwait(false);
        var target = await destination.ValidateAsync(source, directory, fileName, cancellationToken).ConfigureAwait(false);
        if (engine.SupportsFreshRestart) await destination.ValidateProtectedAsync(target.Directory, cancellationToken).ConfigureAwait(false);
        var entry = BuildIntent(source, target, clock.GetUtcNow());
        var jobId = entry.Job.Id;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await repository.ContainsDestinationAsync(target.FullPath, cancellationToken).ConfigureAwait(false))
                throw new DownloadRequestException("History already contains this destination. Choose a different filename or folder.");
            // Durable intent commits BEFORE any mutation is sent to aria2.
            await repository.InsertIntentAsync(entry, cancellationToken).ConfigureAwait(false);
            _inFlight.Add(jobId); _epoch++;
            store.Publish(store.Current.Downloads.Append(entry), store.Current.Connection);
        }
        finally { _gate.Release(); }
        await DispatchAddAsync(entry, unknownSize, cancellationToken).ConfigureAwait(false);
    }

    internal static DownloadEntry BuildIntent(Uri source, DownloadTarget target, DateTimeOffset now)
    {
        var jobId = Guid.NewGuid(); var attemptId = Guid.NewGuid();
        return new(new(jobId, target.FileName, source, target.FullPath, now), new(attemptId, jobId, NewGid(), now),
            new(Guid.NewGuid(), jobId, attemptId, target.FileName, null), new(DownloadStatus.Queued, 0, null, 0),
            new(Guid.NewGuid(), jobId, DownloadAction.Add, OperationStatus.Pending, now));
    }
    private async Task DispatchAddAsync(DownloadEntry entry, bool unknownSize, CancellationToken cancellationToken)
    {
        var operation = entry.PendingOperation!; var jobId = entry.Job.Id;
        try
        {
            // Network waits do not hold the state-update gate. Polling can continue.
            var result = await engine.AddAsync(new(entry.Attempt.EngineId, entry.Job.Source, unknownSize,
                Path.GetDirectoryName(entry.Job.Destination)!, entry.File.FileName, ExactSourceUrl: entry.ExactSourceUrl,
                ContinuityJobId: entry.Job.Id, ContinuityAttemptId: entry.Attempt.Id,
                // ExactSourceUrl is assigned by external intake and restored from its durable ledger.
                // Request JSON cannot select the manual-origin policy.
                RequirePublicOrigin: entry.ExactSourceUrl is not null), cancellationToken).ConfigureAwait(false);
            await CommitAsync(ApplyObservation(entry, result), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var rejected = ex is EngineException { Failure: EngineFailure.RequestRejected };
            await CommitAsync(entry with
            {
                PendingOperation = operation with { Status = rejected ? OperationStatus.Failed : OperationStatus.Uncertain },
                Progress = rejected ? new(DownloadStatus.Failed, 0, null, 0) : entry.Progress,
                ErrorMessage = rejected ? "The engine rejected this download. No retry was sent."
                    : "Add outcome is uncertain. Checking the saved engine ID; no second Add will be sent."
            }, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try { _inFlight.Remove(jobId); _epoch++; }
            finally { _gate.Release(); }
        }
    }

    private async Task CommitAsync(DownloadEntry entry, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var current = store.Current.Downloads.SingleOrDefault(item => item.Attempt.Id == entry.Attempt.Id);
            if (current is null) return;
            if (!DownloadStateTransitions.CanTransition(current.Progress.Status, entry.Progress.Status)) entry = current;
            entry = entry with { ObservedAt = clock.GetUtcNow() };
            var completed = await repository.SaveAndReportCompletionsAsync([entry], token).ConfigureAwait(false);
            _checkpoints[entry.Job.Id] = clock.GetUtcNow();
            if (entry.PendingOperation?.Status == OperationStatus.Failed) entry = entry with { PendingOperation = null };
            store.Publish(store.Current.Downloads.Select(item => item.Job.Id == entry.Job.Id ? entry : item), store.Current.Connection,
                completion: completed.Contains(entry.Attempt.Id) ? new(entry.Attempt.Id, entry.Job.DisplayName) : null);
        }
        finally { _gate.Release(); }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        long epoch;
        DownloadEntry[] requestedEntries;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { epoch = _epoch; requestedEntries = store.Current.Downloads.Where(NeedsObservation).ToArray(); }
        finally { _gate.Release(); }
        var sequence = Interlocked.Increment(ref _issuedPoll);
        var requested = requestedEntries.Select(item => item.Attempt.EngineId).ToHashSet(StringComparer.Ordinal);
        var batch = await engine.ObserveAsync(requested.ToArray(), cancellationToken).ConfigureAwait(false);
        var observed = batch.Downloads
            .ToDictionary(item => item.EngineId, StringComparer.Ordinal);
        int? configureLimit = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A poll captured before a mutation/reconnection, or an older overlapping poll,
            // cannot overwrite the command's newer evidence.
            if (epoch != _epoch || sequence <= _appliedPoll) return;
            _appliedPoll = sequence;
            var generationChanged = _generation != batch.Generation;
            if (generationChanged) { _generation = batch.Generation; _epoch++; }
            if (_configuredGeneration != batch.Generation)
            {
                _configuredGeneration = batch.Generation;
                if (_policy.RequestStatus is null && batch.MaxConcurrentDownloads != _policy.Limit) configureLimit = _policy.Limit;
            }
            if (_policy.RequestedLimit == batch.MaxConcurrentDownloads && _policy.RequestStatus is not null)
            {
                _policy = new(batch.MaxConcurrentDownloads);
                await repository.SaveQueuePolicyAsync(_policy, cancellationToken).ConfigureAwait(false);
            }
            var save = new List<DownloadEntry>();
            var neverSent = repository is IExternalRequestRepository external
                ? (await external.LoadNeverSentExternalJobsAsync(cancellationToken).ConfigureAwait(false)).ToHashSet() : [];
            var now = clock.GetUtcNow();
            var updated = store.Current.Downloads.Select(entry =>
            {
                if (!requested.Contains(entry.Attempt.EngineId) || _inFlight.Contains(entry.Job.Id)
                    || neverSent.Contains(entry.Job.Id) && IsDispatchableExternalIntent(entry)) return entry;
                DownloadEntry next;
                if (observed.TryGetValue(entry.Attempt.EngineId, out var item))
                {
                    var basis = generationChanged && batch.RestoredIds?.Contains(entry.Attempt.EngineId) == true && !IsTerminal(entry.Progress.Status)
                        ? entry with { Progress = new(DownloadStatus.Interrupted, entry.Progress.DownloadedBytes, entry.Progress.TotalBytes, 0) } : entry;
                    next = DownloadStateTransitions.CanTransition(basis.Progress.Status, item.Progress.Status) ? ApplyObservation(basis, item) : entry;
                }
                else if (NeedsObservation(entry)) next = entry with
                {
                    Progress = new(entry.DesiredRemoved ? DownloadStatus.Removed : DownloadStatus.Interrupted, entry.Progress.DownloadedBytes, entry.Progress.TotalBytes, 0),
                    PendingOperation = entry.DesiredRemoved ? null : entry.PendingOperation is null ? null : entry.PendingOperation with { Status = OperationStatus.Uncertain },
                    ErrorMessage = entry.DesiredRemoved ? "Removal intent reconciled: this owned engine has no running transfer. Files were kept."
                        : "Transfer is missing. Use Recover to check its files and restore the same ID; existing files without control files are preserved."
                };
                else return entry;
                if (next != entry) next = next with { ObservedAt = now };
                if (next != entry && (next.Progress.Status != entry.Progress.Status || next.PendingOperation != entry.PendingOperation
                    || next.ErrorMessage != entry.ErrorMessage || next.Progress.TotalBytes != entry.Progress.TotalBytes
                    || !_checkpoints.TryGetValue(entry.Job.Id, out var last) || now - last >= TimeSpan.FromSeconds(5))) save.Add(next);
                return next;
            }).ToArray();
            IReadOnlyList<Guid> completed = Array.Empty<Guid>();
            if (save.Count > 0)
            {
                completed = await repository.SaveAndReportCompletionsAsync(save, cancellationToken).ConfigureAwait(false);
                foreach (var entry in save) _checkpoints[entry.Job.Id] = now;
            }
            var notice = updated.LastOrDefault(item => completed.Contains(item.Attempt.Id));
            store.Publish(updated, EngineConnectionState.Connected, batch.UnknownTransferCount > 0 ? $"{batch.UnknownTransferCount} unrecognized engine transfers need review; they were not changed." : null,
                policy: _policy, observedConcurrency: batch.MaxConcurrentDownloads,
                completion: notice is null ? null : new(notice.Attempt.Id, notice.Job.DisplayName));
        }
        finally { _gate.Release(); }
        if (configureLimit is int limit) await SetConcurrencyAsync(limit, cancellationToken).ConfigureAwait(false);
    }

    private static bool NeedsObservation(DownloadEntry entry) => entry.Progress.Status is not
        (DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Removed)
        || entry.PendingOperation?.Status is OperationStatus.Pending or OperationStatus.Uncertain;
    private static bool IsTerminal(DownloadStatus status) => status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Removed;
    private static DownloadEntry ApplyObservation(DownloadEntry entry, EngineDownload observation)
    {
        var operation = entry.PendingOperation;
        var confirmed = operation is null || IsTerminal(observation.Progress.Status) || operation.Action switch
        {
            DownloadAction.Add or DownloadAction.Retry or DownloadAction.Recover => true,
            DownloadAction.Pause => observation.Progress.Status == DownloadStatus.Paused,
            DownloadAction.Resume => observation.Progress.Status is DownloadStatus.Queued or DownloadStatus.Downloading,
            DownloadAction.Remove => observation.Progress.Status == DownloadStatus.Removed,
            DownloadAction.Move => observation.QueuePosition == operation.QueuePosition || observation.Progress.Status == DownloadStatus.Downloading,
            _ => false
        };
        return entry with
        {
            Progress = observation.Progress, File = entry.File with { TotalBytes = observation.Progress.TotalBytes },
            PendingOperation = confirmed ? null : operation, QueuePosition = observation.QueuePosition,
            ErrorMessage = confirmed ? observation.ErrorMessage : "Command is awaiting engine confirmation. No duplicate command was sent."
        };
    }

    private static string NewGid()
    {
        string gid;
        do { gid = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(); } while (gid == "0000000000000000");
        return gid;
    }

    public Task PauseAsync(Guid jobId, CancellationToken token = default) => CommandAsync(jobId, DownloadAction.Pause, null, token);
    public Task ResumeAsync(Guid jobId, CancellationToken token = default) => CommandAsync(jobId, DownloadAction.Resume, null, token);
    public Task RemoveAsync(Guid jobId, CancellationToken token = default) => CommandAsync(jobId, DownloadAction.Remove, null, token);
    public Task MoveAsync(Guid jobId, int offset, CancellationToken token = default) => CommandAsync(jobId, DownloadAction.Move, offset, token);
    public Task RecoverAsync(Guid jobId, CancellationToken token = default) => CommandAsync(jobId, DownloadAction.Recover, null, token);

    public async Task CheckpointAsync(CancellationToken token = default)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { await repository.SaveAsync(store.Current.Downloads, token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private DownloadEntry Find(Guid jobId) => store.Current.Downloads.SingleOrDefault(item => item.Job.Id == jobId)
        ?? throw new DownloadRequestException("This download is no longer available.");

    private async Task CommandAsync(Guid jobId, DownloadAction action, int? offset, CancellationToken token)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        if (IsTerminal((await GetEntryAsync(jobId, token).ConfigureAwait(false)).Progress.Status)) return;
        var jobGate = _jobGates.GetOrAdd(jobId, _ => new(1, 1));
        await jobGate.WaitAsync(token).ConfigureAwait(false);
        DownloadEntry? entry = null;
        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var current = Find(jobId);
                if (current.PendingOperation is not null && !(action == DownloadAction.Recover && current.PendingOperation.Status == OperationStatus.Uncertain))
                    throw new DownloadRequestException("Wait for the previous command to be reconciled.");
                var status = current.Progress.Status;
                if (IsTerminal(status)) return; // A completion racing a click wins; no stale command is sent.
                if (action == DownloadAction.Recover && status != DownloadStatus.Interrupted && current.PendingOperation?.Status != OperationStatus.Uncertain) return;
                if (status == DownloadStatus.Interrupted && action != DownloadAction.Recover) throw new DownloadRequestException("This transfer needs recovery before it can be controlled.");
                if (action == DownloadAction.Pause && status == DownloadStatus.Paused) return;
                if (action == DownloadAction.Resume && status != DownloadStatus.Paused) return;
                int? position = action == DownloadAction.Recover ? current.PendingOperation?.QueuePosition : null;
                if (action == DownloadAction.Move)
                {
                    if (status != DownloadStatus.Queued || current.QueuePosition is not int from)
                        throw new DownloadRequestException("Only waiting downloads can change queue position.");
                    if (offset is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(offset));
                    var last = store.Current.Downloads.Max(item => item.QueuePosition ?? 0);
                    position = Math.Clamp(from + offset.Value, 0, last);
                    if (position == from) return;
                }
                entry = current with { PendingOperation = new(Guid.NewGuid(), jobId, action, OperationStatus.Pending, clock.GetUtcNow(), position), ErrorMessage = null,
                    DesiredPaused = action == DownloadAction.Pause || action != DownloadAction.Resume && current.DesiredPaused,
                    DesiredRemoved = action == DownloadAction.Remove || current.DesiredRemoved };
                if (action == DownloadAction.Recover) await repository.InsertRecoveryOperationAsync(entry, token).ConfigureAwait(false);
                else await repository.InsertOperationAsync(entry, token).ConfigureAwait(false);
                _inFlight.Add(jobId); _epoch++;
                store.Publish(store.Current.Downloads.Select(item => item.Job.Id == jobId ? entry : item), store.Current.Connection);
            }
            finally { _gate.Release(); }
            try
            {
                var result = action == DownloadAction.Recover
                    ? new EngineCommandResult(_generation ?? "recovered", await engine.RecoverAsync(entry, token).ConfigureAwait(false))
                    : await engine.ExecuteAsync(entry.Attempt.EngineId, action, entry.PendingOperation!.QueuePosition, token).ConfigureAwait(false);
                await _gate.WaitAsync().ConfigureAwait(false);
                try { if (_generation != result.Generation) { _generation = result.Generation; _epoch++; } }
                finally { _gate.Release(); }
                var next = result.Download is not null && DownloadStateTransitions.CanTransition(entry.Progress.Status, result.Download.Progress.Status)
                    ? ApplyObservation(entry, result.Download) : entry with
                    { PendingOperation = entry.PendingOperation with { Status = OperationStatus.Uncertain }, ErrorMessage = "Command outcome is uncertain. Checking the saved transfer ID." };
                await CommitAsync(next, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var rejected = ex is EngineException { Failure: EngineFailure.RequestRejected } or DownloadRequestException or ArgumentException;
                await CommitAsync(entry with
                {
                    PendingOperation = entry.PendingOperation! with { Status = rejected ? OperationStatus.Failed : OperationStatus.Uncertain },
                    ErrorMessage = rejected ? "The engine rejected this command. Refreshing its actual status."
                        : "Command outcome is uncertain. Checking the saved transfer ID; no duplicate command will be sent."
                }, CancellationToken.None).ConfigureAwait(false);
                // Failed operations are retained in SQLite, but no longer block a new command.
                if (rejected)
                {
                    await _gate.WaitAsync().ConfigureAwait(false);
                    try { store.Publish(store.Current.Downloads.Select(item => item.Job.Id == jobId ? item with { PendingOperation = null } : item), store.Current.Connection); }
                    finally { _gate.Release(); }
                }
                throw;
            }
        }
        finally
        {
            if (entry is not null)
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                try { _inFlight.Remove(jobId); _epoch++; }
                finally { _gate.Release(); }
            }
            jobGate.Release();
        }
    }

    public bool SupportsFreshRestart => engine.SupportsFreshRestart;
    public Task RestartFreshAsync(Guid jobId, CancellationToken token = default) => RetryCoreAsync(jobId, true, token);
    public Task RetryAsync(Guid jobId, CancellationToken token = default) => RetryCoreAsync(jobId, false, token);
    private async Task RetryCoreAsync(Guid jobId, bool freshRestart, CancellationToken token)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        var jobGate = _jobGates.GetOrAdd(jobId, _ => new(1, 1));
        var requestedEntry = await GetEntryAsync(jobId, token).ConfigureAwait(false);
        if (requestedEntry.PendingOperation is { Action: DownloadAction.Retry, Status: OperationStatus.Pending }) return;
        var requestedAttempt = requestedEntry.Attempt.Id;
        await jobGate.WaitAsync(token).ConfigureAwait(false);
        DownloadEntry? retry = null;
        try
        {
            var previous = await GetEntryAsync(jobId, token).ConfigureAwait(false);
            if (previous.Attempt.Id != requestedAttempt) return; // Coalesce a double-click already handled by another retry.
            if (freshRestart ? !engine.SupportsFreshRestart || previous.Progress.Status != DownloadStatus.Interrupted || previous.PendingOperation?.Status == OperationStatus.Pending
                : previous.PendingOperation is not null || previous.Progress.Status is not (DownloadStatus.Failed or DownloadStatus.Removed))
                throw new DownloadRequestException("Retry is available only for reconciled failed or removed attempts.");
            // Check the old GID first: retry must never compete with an old active writer.
            if (freshRestart)
            {
                if (!await engine.CanRestartFreshAsync(previous.Attempt.EngineId, token).ConfigureAwait(false))
                    throw new DownloadRequestException("This attempt cannot restart from scratch yet. Recover it or reconcile its running transfer first.");
            }
            else
            {
                var evidence = await engine.ObserveAsync([previous.Attempt.EngineId], token).ConfigureAwait(false);
                if (evidence.Downloads.Any(item => !IsTerminal(item.Progress.Status)))
                    throw new DownloadRequestException("The previous attempt is still running or paused. Reconcile it before retrying.");
            }
            var target = freshRestart
                ? await destination.ValidateFreshRestartAsync(previous.Job.Source, Path.GetDirectoryName(previous.Job.Destination)!, previous.File.FileName, token).ConfigureAwait(false)
                : await destination.ValidateRetryAsync(previous.Job.Source, Path.GetDirectoryName(previous.Job.Destination)!, previous.File.FileName, token).ConfigureAwait(false);
            if (engine.SupportsFreshRestart) await destination.ValidateProtectedAsync(target.Directory, token).ConfigureAwait(false);
            var now = clock.GetUtcNow();
            if (now <= previous.Attempt.StartedAt) now = previous.Attempt.StartedAt.AddTicks(1);
            var attempt = new TransferAttempt(Guid.NewGuid(), jobId, NewGid(), now, previous.Attempt.Id);
            retry = previous with { Attempt = attempt, File = new(Guid.NewGuid(), jobId, attempt.Id, target.FileName, null),
                DesiredPaused = false, DesiredRemoved = false,
                Progress = new(DownloadStatus.Queued, 0, null, 0), QueuePosition = null, ErrorMessage = null,
                PendingOperation = new(Guid.NewGuid(), jobId, DownloadAction.Retry, OperationStatus.Pending, now) };
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (store.Current.Downloads.SingleOrDefault(row => row.Job.Id == jobId) is { } current && current.Attempt.Id != previous.Attempt.Id)
                    throw new DownloadRequestException("The attempt changed. Refresh before retrying.");
                if (freshRestart) await repository.InsertFreshRestartIntentAsync(previous, retry, token).ConfigureAwait(false);
                else await repository.InsertRetryIntentAsync(previous, retry, token).ConfigureAwait(false);
                _inFlight.Add(jobId); _epoch++;
                store.Publish(store.Current.Downloads.Where(item => item.Job.Id != jobId).Append(retry), store.Current.Connection);
            }
            finally { _gate.Release(); }
            try
            {
                var result = await engine.AddAsync(new(attempt.EngineId, previous.Job.Source, Directory: target.Directory,
                    FileName: target.FileName, ContinueExisting: target.ContinueExisting, ExactSourceUrl: retry.ExactSourceUrl,
                    ContinuityJobId: retry.Job.Id, ContinuityAttemptId: retry.Attempt.Id,
                    RequirePublicOrigin: retry.ExactSourceUrl is not null, LegacyParentEngineId: previous.Attempt.EngineId, FreshRestart: freshRestart), token).ConfigureAwait(false);
                await CommitAsync(ApplyObservation(retry, result), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var rejected = ex is EngineException { Failure: EngineFailure.RequestRejected };
                await CommitAsync(retry with
                {
                    PendingOperation = retry.PendingOperation! with { Status = rejected ? OperationStatus.Failed : OperationStatus.Uncertain },
                    Progress = rejected ? new(DownloadStatus.Failed, 0, null, 0) : retry.Progress,
                    ErrorMessage = rejected ? "The engine rejected retry." : "Retry outcome is uncertain. Checking the new saved ID without resubmitting."
                }, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            if (retry is not null)
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                try { _inFlight.Remove(jobId); _epoch++; }
                finally { _gate.Release(); }
            }
            jobGate.Release();
        }
    }

    public async Task SetConcurrencyAsync(int limit, CancellationToken token = default)
    {
        if (limit is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(limit), "Choose between 1 and 16 simultaneous downloads.");
        await InitializeAsync(token).ConfigureAwait(false);
        await _policyGate.WaitAsync(token).ConfigureAwait(false);
        QueuePolicy? intent = null;
        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_policy.RequestStatus is not null) throw new DownloadRequestException("Wait for the previous queue setting to be reconciled.");
                intent = _policy with { RequestedLimit = limit, OperationId = Guid.NewGuid(), RequestStatus = OperationStatus.Pending };
                await repository.SaveQueuePolicyAsync(intent, token).ConfigureAwait(false);
                _policy = intent; _epoch++;
                store.Publish(store.Current.Downloads, store.Current.Connection, policy: _policy);
            }
            finally { _gate.Release(); }
            try
            {
                await engine.SetConcurrencyAsync(limit, token).ConfigureAwait(false);
                await SavePolicyAsync(new(limit), limit).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var rejected = ex is EngineException { Failure: EngineFailure.RequestRejected };
                await SavePolicyAsync(rejected ? new(intent.Limit) : intent with { RequestStatus = OperationStatus.Uncertain }, null).ConfigureAwait(false);
                throw;
            }
        }
        finally { _policyGate.Release(); }
    }
    private async Task SavePolicyAsync(QueuePolicy policy, int? observed)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await repository.SaveQueuePolicyAsync(policy, CancellationToken.None).ConfigureAwait(false);
            _policy = policy; _epoch++;
            store.Publish(store.Current.Downloads, store.Current.Connection, policy: policy, observedConcurrency: observed);
        }
        finally { _gate.Release(); }
    }
    public async Task ReportFailureAsync(string message)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { store.Publish(store.Current.Downloads, EngineConnectionState.Disconnected, message); }
        finally { _gate.Release(); }
    }
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0) throw new InvalidOperationException("There must be only one synchronization loop.");
        try
        {
            do
            {
                try { await RefreshAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (DownloadStorageException ex) { await ReportFailureAsync(ex.Message).ConfigureAwait(false); }
                catch (Exception) { await ReportFailureAsync("Live updates are unavailable. Transfers may still be running; reconnecting…").ConfigureAwait(false); }
                await Task.Delay(PollDelay, clock, cancellationToken).ConfigureAwait(false);
            } while (!cancellationToken.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { Volatile.Write(ref _running, 0); }
    }

    public TimeSpan PollDelay => store.Current.Connection == EngineConnectionState.Disconnected ? TimeSpan.FromSeconds(5)
        : store.Current.Downloads.Any(row => row.Progress.Status == DownloadStatus.Downloading || row.PendingOperation is not null) ? TimeSpan.FromMilliseconds(500)
        : store.Current.Downloads.Any(NeedsObservation) ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(5);
    private async Task<DownloadEntry> GetEntryAsync(Guid id, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return store.Current.Downloads.SingleOrDefault(row => row.Job.Id == id)
                ?? await repository.GetAsync(id, token).ConfigureAwait(false)
                ?? throw new DownloadRequestException("This saved download could not be found. Refresh history.");
        }
        finally { _gate.Release(); }
    }
}
