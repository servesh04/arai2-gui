using AriaGui.App.Services;
using AriaGui.App.ViewModels;
using AriaGui.Application.Downloads;
using AriaGui.Application.Engine;
using AriaGui.Core.Downloads;

namespace AriaGui.Application.Tests;

public sealed class QueueCommandTests
{
    [Fact]
    public async Task Intent_precedes_rpc_double_pause_is_coalesced_and_rapid_resume_is_serialized()
    {
        var rig = new Rig();
        var entry = await rig.Add();
        using var view = new DownloadsViewModel(rig.Coordinator, rig.Store, new ImmediateDispatcher());
        await rig.Coordinator.RefreshAsync();
        var entered = Signal(); var release = Signal();
        rig.Engine.BeforeExecute = async (_, action) =>
        {
            if (action != DownloadAction.Pause) return;
            Assert.Equal(DownloadAction.Pause, rig.Repository.Entries[entry.Job.Id].PendingOperation?.Action);
            entered.SetResult(); await release.Task;
        };
        var pause = rig.Coordinator.PauseAsync(entry.Job.Id);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("Pausing…", Assert.Single(view.Downloads).Status);
        Assert.False(view.Downloads[0].ResumeCommand.CanExecute(null));
        var duplicate = rig.Coordinator.PauseAsync(entry.Job.Id);
        var resume = rig.Coordinator.ResumeAsync(entry.Job.Id);
        release.SetResult();
        await Task.WhenAll(pause, duplicate, resume);
        Assert.Equal(new[] { DownloadAction.Pause, DownloadAction.Resume }, rig.Engine.Commands.Select(item => item.Action));
        Assert.Equal(DownloadStatus.Downloading, rig.Store.Current.Downloads[0].Progress.Status);
    }

    [Fact]
    public async Task Failed_intent_write_prevents_an_engine_mutation()
    {
        var rig = new Rig(); var entry = await rig.Add();
        rig.Repository.RejectCommand = true;
        await Assert.ThrowsAsync<DownloadStorageException>(() => rig.Coordinator.RemoveAsync(entry.Job.Id));
        Assert.Empty(rig.Engine.Commands);
        Assert.Null(rig.Store.Current.Downloads[0].PendingOperation);
    }

    [Fact]
    public async Task Lost_pause_reply_is_reconciled_after_coordinator_restart_without_resending()
    {
        var rig = new Rig(); var entry = await rig.Add();
        rig.Engine.LoseReply = true;
        await Assert.ThrowsAsync<EngineException>(() => rig.Coordinator.PauseAsync(entry.Job.Id));
        Assert.Equal(DownloadStatus.Downloading, rig.Store.Current.Downloads[0].Progress.Status);
        Assert.Equal(OperationStatus.Uncertain, rig.Repository.Entries[entry.Job.Id].PendingOperation?.Status);
        var store = new DownloadRuntimeStore();
        await rig.Create(store).RefreshAsync();
        Assert.Equal(DownloadStatus.Paused, Assert.Single(store.Current.Downloads).Progress.Status);
        Assert.Null(store.Current.Downloads[0].PendingOperation);
        Assert.Single(rig.Engine.Commands);
    }

    [Fact]
    public async Task Timeout_without_acceptance_keeps_resume_uncertain_until_actual_evidence_matches()
    {
        var rig = new Rig(); var entry = await rig.Add();
        await rig.Coordinator.PauseAsync(entry.Job.Id);
        rig.Engine.BeforeExecute = (_, _) => throw new EngineException(EngineFailure.RpcUnavailable, "No acceptance.");
        await Assert.ThrowsAsync<EngineException>(() => rig.Coordinator.ResumeAsync(entry.Job.Id));
        await rig.Coordinator.RefreshAsync();
        Assert.Equal(DownloadStatus.Paused, rig.Store.Current.Downloads[0].Progress.Status);
        Assert.Equal(OperationStatus.Uncertain, rig.Store.Current.Downloads[0].PendingOperation?.Status);
        rig.Engine.Set(entry.Attempt.EngineId, DownloadStatus.Downloading);
        await rig.Coordinator.RefreshAsync();
        Assert.Null(rig.Store.Current.Downloads[0].PendingOperation);
        Assert.Equal(2, rig.Engine.Commands.Count);
    }

    [Fact]
    public async Task A_poll_captured_before_pause_cannot_undo_the_confirmed_pause()
    {
        var rig = new Rig(); var entry = await rig.Add();
        var entered = Signal(); var release = Signal();
        rig.Engine.DelayNextObservation = async () => { entered.SetResult(); await release.Task; };
        var stale = rig.Coordinator.RefreshAsync();
        await entered.Task;
        await rig.Coordinator.PauseAsync(entry.Job.Id);
        release.SetResult(); await stale;
        Assert.Equal(DownloadStatus.Paused, rig.Store.Current.Downloads[0].Progress.Status);
    }

    [Fact]
    public async Task An_old_session_poll_cannot_replace_a_newer_connection_observation()
    {
        var rig = new Rig(); var entry = await rig.Add();
        await rig.Coordinator.RefreshAsync();
        var entered = Signal(); var release = Signal();
        rig.Engine.DelayNextObservation = async () => { entered.SetResult(); await release.Task; };
        var stale = rig.Coordinator.RefreshAsync(); await entered.Task;
        rig.Engine.Generation = "new-session";
        rig.Engine.Set(entry.Attempt.EngineId, DownloadStatus.Paused);
        await rig.Coordinator.RefreshAsync();
        release.SetResult(); await stale;
        Assert.Equal(DownloadStatus.Paused, rig.Store.Current.Downloads[0].Progress.Status);
    }

    [Fact]
    public async Task Completion_racing_pause_wins_and_its_notice_is_deduplicated_by_attempt()
    {
        var rig = new Rig(); var entry = await rig.Add();
        rig.Engine.CompleteInstead = true;
        await rig.Coordinator.PauseAsync(entry.Job.Id);
        Assert.Equal(DownloadStatus.Completed, rig.Store.Current.Downloads[0].Progress.Status);
        Assert.Equal(entry.Attempt.Id, rig.Store.Current.LatestCompletion?.AttemptId);
        Assert.Empty(await rig.Repository.SaveAndReportCompletionsAsync(rig.Store.Current.Downloads, default));
        var restarted = new DownloadRuntimeStore(); await rig.Create(restarted).RefreshAsync();
        Assert.Null(restarted.Current.LatestCompletion);
        await rig.Coordinator.ResumeAsync(entry.Job.Id);
        Assert.Single(rig.Engine.Commands);
    }

    [Fact]
    public async Task Remove_is_terminal_and_retry_keeps_the_job_but_creates_one_new_linked_attempt()
    {
        var rig = new Rig(); var entry = await rig.Add();
        await rig.Coordinator.RemoveAsync(entry.Job.Id);
        Assert.Equal(DownloadStatus.Removed, rig.Store.Current.Downloads[0].Progress.Status);
        var entered = Signal(); var release = Signal();
        rig.Engine.BeforeAdd = async () => { entered.SetResult(); await release.Task; };
        var retry = rig.Coordinator.RetryAsync(entry.Job.Id); await entered.Task;
        var duplicate = rig.Coordinator.RetryAsync(entry.Job.Id);
        release.SetResult(); await Task.WhenAll(retry, duplicate);
        var next = Assert.Single(rig.Store.Current.Downloads);
        Assert.Equal(entry.Job, next.Job);
        Assert.Equal(entry.Attempt.Id, next.Attempt.ParentAttemptId);
        Assert.NotEqual(entry.Attempt.EngineId, next.Attempt.EngineId);
        Assert.Equal(2, rig.Engine.AddCount);
        Assert.Equal(2, rig.Repository.Attempts.Count);
    }

    [Fact]
    public async Task Retry_refuses_a_previous_attempt_that_is_still_an_active_writer()
    {
        var rig = new Rig(); var entry = await rig.Add();
        rig.Engine.Set(entry.Attempt.EngineId, DownloadStatus.Failed);
        await rig.Coordinator.RefreshAsync();
        rig.Engine.Set(entry.Attempt.EngineId, DownloadStatus.Downloading);
        await Assert.ThrowsAsync<DownloadRequestException>(() => rig.Coordinator.RetryAsync(entry.Job.Id));
        Assert.Equal(1, rig.Engine.AddCount);
        Assert.Single(rig.Repository.Attempts);
    }

    [Fact]
    public async Task Reorder_sends_an_absolute_waiting_position_and_never_pauses_active_jobs()
    {
        var rig = new Rig(); var active = await rig.Add("active.bin");
        rig.Engine.InitialStatus = DownloadStatus.Queued;
        var first = await rig.Add("first.bin"); var last = await rig.Add("last.bin");
        rig.Engine.Set(first.Attempt.EngineId, DownloadStatus.Queued, 0);
        rig.Engine.Set(last.Attempt.EngineId, DownloadStatus.Queued, 1);
        await rig.Coordinator.RefreshAsync();
        await rig.Coordinator.MoveAsync(last.Job.Id, -1);
        await rig.Coordinator.RefreshAsync();
        var command = Assert.Single(rig.Engine.Commands);
        Assert.Equal(DownloadAction.Move, command.Action); Assert.Equal(0, command.Position);
        Assert.Equal(DownloadStatus.Downloading, rig.Store.Current.Downloads.Single(item => item.Job.Id == active.Job.Id).Progress.Status);
        using var view = new DownloadsViewModel(rig.Coordinator, rig.Store, new ImmediateDispatcher());
        Assert.Equal(last.Job.Id, view.Downloads[1].Id);
    }

    [Fact]
    public async Task Saved_concurrency_is_applied_to_a_new_session_after_durable_intent()
    {
        var rig = new Rig(); await rig.Coordinator.RefreshAsync();
        rig.Engine.BeforeLimit = limit => Assert.Equal(limit, rig.Repository.Policy.RequestedLimit);
        await rig.Coordinator.SetConcurrencyAsync(1);
        Assert.Equal(1, rig.Repository.Policy.Limit);
        rig.Engine.Limit = 3; rig.Engine.Generation = "restart";
        var store = new DownloadRuntimeStore(); await rig.Create(store).RefreshAsync();
        Assert.Equal(1, rig.Engine.Limit); Assert.Null(rig.Repository.Policy.RequestStatus);
        using var view = new DownloadsViewModel(rig.Create(store), store, new ImmediateDispatcher());
        Assert.Equal(1d, view.ConcurrencyLimit);
    }

    [Fact]
    public async Task Uncertain_limit_is_not_reissued_or_falsely_confirmed()
    {
        var rig = new Rig(); await rig.Coordinator.RefreshAsync();
        rig.Engine.BeforeLimit = _ => throw new EngineException(EngineFailure.RpcUnavailable, "Lost connection.");
        await Assert.ThrowsAsync<EngineException>(() => rig.Coordinator.SetConcurrencyAsync(1));
        var store = new DownloadRuntimeStore(); var restarted = rig.Create(store);
        await restarted.RefreshAsync();
        Assert.Equal(OperationStatus.Uncertain, store.Current.QueuePolicy?.RequestStatus);
        Assert.Equal(3, store.Current.ObservedConcurrency);
        Assert.Equal(1, rig.Engine.LimitCalls);
        rig.Engine.Limit = 1;
        await restarted.RefreshAsync();
        Assert.Null(store.Current.QueuePolicy?.RequestStatus);
        Assert.Equal(1, rig.Repository.Policy.Limit);
    }

    [Fact]
    public async Task Disconnection_disables_row_commands_without_marking_the_transfer_failed()
    {
        var rig = new Rig(); await rig.Add(); await rig.Coordinator.RefreshAsync();
        using var view = new DownloadsViewModel(rig.Coordinator, rig.Store, new ImmediateDispatcher());
        Assert.True(view.Downloads[0].PauseCommand.CanExecute(null));
        await rig.Coordinator.ReportFailureAsync("Unavailable");
        Assert.False(view.Downloads[0].PauseCommand.CanExecute(null));
        Assert.Equal("Downloading", view.Downloads[0].Status);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class ImmediateDispatcher : IUiDispatcher { public void Enqueue(Action action) => action(); }
    private sealed class Rig
    {
        public MemoryDownloadRepository Repository { get; } = new();
        public CommandEngine Engine { get; } = new();
        public DownloadRuntimeStore Store { get; } = new();
        public DownloadCoordinator Coordinator { get; }
        public Rig() { Coordinator = Create(Store); }
        public DownloadCoordinator Create(DownloadRuntimeStore store) => new(Engine, store, TimeProvider.System, Repository, new TestDownloadDestination());
        public async Task<DownloadEntry> Add(string name = "file.bin")
        { await Coordinator.AddAsync("https://example.com/" + name); return Store.Current.Downloads.Last(); }
    }
    private sealed class CommandEngine : IAria2Client
    {
        public Dictionary<string, EngineDownload> Transfers { get; } = [];
        public List<(DownloadAction Action, int? Position)> Commands { get; } = [];
        public Func<string, DownloadAction, Task>? BeforeExecute;
        public Func<Task>? BeforeAdd, DelayNextObservation;
        public Action<int>? BeforeLimit;
        public bool LoseReply, CompleteInstead;
        public int AddCount, LimitCalls, Limit = 3;
        public string Generation = "initial-session";
        public DownloadStatus InitialStatus = DownloadStatus.Downloading;
        public async Task<EngineDownload> AddAsync(EngineDownloadRequest request, CancellationToken token)
        {
            AddCount++; if (BeforeAdd is not null) await BeforeAdd();
            var download = new EngineDownload(request.EngineId, new(InitialStatus, 10, 1000, 20));
            Transfers.Add(request.EngineId, download); return download;
        }
        public void Set(string gid, DownloadStatus status, int? position = null) =>
            Transfers[gid] = Transfers[gid] with { Progress = new(status, status == DownloadStatus.Completed ? 1000 : 10, 1000, 0), QueuePosition = position };
        public Task<IReadOnlyList<EngineDownload>> GetDownloadsAsync(IReadOnlyList<string> ids, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<EngineDownload>>(Transfers.Values.Where(item => ids.Contains(item.EngineId)).ToArray());
        public async Task<EngineObservation> ObserveAsync(IReadOnlyList<string> ids, CancellationToken token)
        {
            var snapshot = new EngineObservation(Generation, await GetDownloadsAsync(ids, token), Limit);
            var delay = DelayNextObservation; DelayNextObservation = null;
            if (delay is not null) await delay();
            return snapshot;
        }
        public async Task<EngineCommandResult> ExecuteAsync(string gid, DownloadAction action, int? position, CancellationToken token)
        {
            Commands.Add((action, position));
            if (BeforeExecute is not null) await BeforeExecute(gid, action);
            if (CompleteInstead) Set(gid, DownloadStatus.Completed);
            else if (action == DownloadAction.Move)
            {
                var from = Transfers[gid].QueuePosition!.Value;
                foreach (var item in Transfers.Values.ToArray())
                    if (item.EngineId != gid && item.QueuePosition is int p)
                        Transfers[item.EngineId] = item with { QueuePosition = from > position && p >= position && p < from ? p + 1
                            : from < position && p > from && p <= position ? p - 1 : p };
                Transfers[gid] = Transfers[gid] with { QueuePosition = position };
            }
            else Set(gid, action switch { DownloadAction.Pause => DownloadStatus.Paused, DownloadAction.Resume => DownloadStatus.Downloading, _ => DownloadStatus.Removed });
            if (LoseReply) throw new EngineException(EngineFailure.RpcUnavailable, "Accepted but reply lost.");
            return new(Generation, Transfers[gid]);
        }
        public Task SetConcurrencyAsync(int limit, CancellationToken token)
        { LimitCalls++; BeforeLimit?.Invoke(limit); Limit = limit; return Task.CompletedTask; }
    }
}
