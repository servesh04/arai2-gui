using AriaGui.App.Services;
using AriaGui.App.ViewModels;
using AriaGui.Application.Downloads;
using AriaGui.Application.Engine;
using AriaGui.Core.Downloads;

namespace AriaGui.Application.Tests;

public sealed class QueueCommandTests
{
    [Theory]
    [InlineData(" ")]
    [InlineData("abc")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void Expected_sha256_requires_exact_hex(string malformed) =>
        Assert.Throws<ArgumentException>(() => ExpectedChecksum.FromManualInput(malformed));

    [Fact]
    public async Task Required_checksum_cannot_complete_from_unverified_engine_observation()
    {
        var rig = new Rig();
        await rig.Coordinator.AddAsync("https://example.com/required.bin", expectedSha256: new string('0', 64));
        var gid = Assert.Single(rig.Store.Current.Downloads).Attempt.EngineId;
        rig.Engine.Set(gid, DownloadStatus.Completed);
        await rig.Coordinator.RefreshAsync();
        var entry = Assert.Single(rig.Store.Current.Downloads);
        Assert.Equal(DownloadStatus.Interrupted, entry.Progress.Status);
        Assert.Equal(VerificationStatus.Pending, entry.Verification);
        Assert.Null(rig.Store.Current.LatestCompletion);
    }
    [Fact]
    public async Task Verifying_state_is_separate_from_transfer_completion_and_visible_in_row()
    {
        var rig = new Rig();
        await rig.Coordinator.AddAsync("https://example.com/required.bin", expectedSha256: new string('0', 64));
        var gid = Assert.Single(rig.Store.Current.Downloads).Attempt.EngineId;
        rig.Engine.Transfers[gid] = rig.Engine.Transfers[gid] with
        { Progress = new(DownloadStatus.Downloading, 1000, 1000, 0), Verification = VerificationStatus.Verifying };
        await rig.Coordinator.RefreshAsync();
        using var view = new DownloadsViewModel(rig.Coordinator, rig.Store, new ImmediateDispatcher());
        var row = Assert.Single(view.Downloads);
        Assert.Equal("Verifying SHA-256…", row.Status);
        Assert.Equal("Verification: Checking SHA-256…", row.VerificationLabel);
        Assert.False(row.ShowPause);
        Assert.Null(rig.Store.Current.LatestCompletion);
    }
    [Fact]
    public async Task Disabled_checksum_restore_has_explicit_row_reason_without_resuming()
    {
        var rig = new Rig();
        await rig.Coordinator.AddAsync("https://example.com/required.bin", expectedSha256: new string('0', 64));
        var gid = Assert.Single(rig.Store.Current.Downloads).Attempt.EngineId;
        rig.Engine.ChecksumEnabled = false;
        rig.Engine.Transfers.Remove(gid);
        await rig.Coordinator.RefreshAsync();
        var held = Assert.Single(rig.Store.Current.Downloads);
        Assert.Equal(DownloadStatus.Interrupted, held.Progress.Status);
        Assert.Contains("Required SHA-256 verification is unavailable", held.ErrorMessage);
        Assert.Equal(VerificationStatus.Pending, held.Verification);
    }
    [Fact]
    public async Task Blocked_schedule_stages_fresh_and_retry_jobs_paused_before_add_rpc()
    {
        var clock = new ScheduleClock(new DateTimeOffset(2026, 10, 12, 10, 6, 0, TimeSpan.Zero));
        var rig = new Rig(clock);
        await rig.Coordinator.SetBandwidthScheduleAsync(new("UTC", BandwidthProfile.Unlimited,
            [new(DayOfWeek.Monday, 605, 610, ScheduleAction.Block)], Enabled: true));
        var fresh = await rig.Add("fresh.bin");
        Assert.True(rig.Engine.LastAddedRequest!.StartPaused);
        Assert.True(rig.Repository.Entries[fresh.Job.Id].PolicyHeld);
        Assert.False(rig.Repository.Entries[fresh.Job.Id].DesiredPaused);
        Assert.Equal(DownloadStatus.Paused, fresh.Progress.Status);

        var retryRig = new Rig(clock);
        var first = await retryRig.Add("retry.bin");
        retryRig.Engine.Set(first.Attempt.EngineId, DownloadStatus.Failed);
        await retryRig.Coordinator.RefreshAsync();
        await retryRig.Coordinator.SetBandwidthScheduleAsync(new("UTC", BandwidthProfile.Unlimited,
            [new(DayOfWeek.Monday, 605, 610, ScheduleAction.Block)], Enabled: true));
        await retryRig.Coordinator.RetryAsync(first.Job.Id);
        Assert.True(retryRig.Engine.LastAddedRequest!.StartPaused);
        Assert.True(retryRig.Repository.Entries[first.Job.Id].PolicyHeld);
        Assert.Equal(DownloadStatus.Paused, retryRig.Store.Current.Downloads.Single().Progress.Status);
    }

    [Fact]
    public async Task Disabling_schedule_restores_default_profile_and_clears_reset_intent()
    {
        var clock = new ScheduleClock(new DateTimeOffset(2026, 10, 12, 10, 6, 0, TimeSpan.Zero));
        var rig = new Rig(clock); await rig.Coordinator.RefreshAsync();
        var defaultProfile = new BandwidthProfile("Normal", new(131072), ByteRateLimit.Unlimited);
        var scheduled = new BandwidthProfile("Metered", new(32768), ByteRateLimit.Unlimited);
        await rig.Coordinator.SetBandwidthScheduleAsync(new("UTC", defaultProfile,
            [new(DayOfWeek.Monday, 605, 610, ScheduleAction.Profile, scheduled)], Enabled: true));
        Assert.Equal(scheduled.Download, rig.Engine.DownloadCap);
        await rig.Coordinator.SetBandwidthScheduleAsync(rig.Repository.Schedule with { Enabled = false });
        Assert.Equal(defaultProfile.Download, rig.Engine.DownloadCap);
        Assert.False(rig.Repository.Schedule.ResetDefaultOnDisable);
        Assert.Equal(2, rig.Engine.BandwidthCalls);
        await rig.Coordinator.RefreshAsync();
        Assert.Equal(2, rig.Engine.BandwidthCalls);
    }

    [Fact]
    public async Task Schedule_releases_only_policy_held_work_after_sleep_and_manual_pause_wins()
    {
        var clock = new ScheduleClock(new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero));
        var rig = new Rig(clock); var entry = await rig.Add(); await rig.Coordinator.RefreshAsync();
        await rig.Coordinator.SetBandwidthScheduleAsync(new("UTC", BandwidthProfile.Unlimited,
            [new(DayOfWeek.Monday, 605, 610, ScheduleAction.Block)], Enabled: true));
        clock.Now = clock.Now.AddMinutes(6);
        await rig.Coordinator.NotifyClockChangedAsync();
        Assert.Equal(DownloadStatus.Paused, rig.Store.Current.Downloads.Single().Progress.Status);
        Assert.True(rig.Repository.Entries[entry.Job.Id].PolicyHeld);
        Assert.False(rig.Repository.Entries[entry.Job.Id].DesiredPaused);
        await rig.Coordinator.PauseAsync(entry.Job.Id); // User intent while already policy-held.
        Assert.True(rig.Repository.Entries[entry.Job.Id].DesiredPaused);
        clock.Now = clock.Now.AddMinutes(20); // Sleep spans the release boundary.
        await rig.Coordinator.NotifyWakeAsync();
        Assert.False(rig.Repository.Entries[entry.Job.Id].PolicyHeld);
        Assert.Equal(DownloadStatus.Paused, rig.Store.Current.Downloads.Single().Progress.Status);
        Assert.Equal([DownloadAction.Pause], rig.Engine.Commands.Select(row => row.Action));
        await rig.Coordinator.ResumeAsync(entry.Job.Id);
        Assert.Equal([DownloadAction.Pause, DownloadAction.Resume], rig.Engine.Commands.Select(row => row.Action));
    }

    [Fact]
    public async Task Schedule_release_resumes_eligible_hold_once_and_never_replays_uncertain_pause()
    {
        var clock = new ScheduleClock(new DateTimeOffset(2026, 10, 12, 10, 6, 0, TimeSpan.Zero));
        var rig = new Rig(clock); var entry = await rig.Add(); await rig.Coordinator.RefreshAsync();
        rig.Engine.LoseReply = true;
        await rig.Coordinator.SetBandwidthScheduleAsync(new("UTC", BandwidthProfile.Unlimited,
            [new(DayOfWeek.Monday, 605, 610, ScheduleAction.Block)], Enabled: true));
        Assert.Single(rig.Engine.Commands);
        await rig.Coordinator.RefreshAsync();
        Assert.Single(rig.Engine.Commands);
        Assert.True(rig.Repository.Entries[entry.Job.Id].PolicyHeld);
        rig.Engine.LoseReply = false;
        clock.Now = clock.Now.AddMinutes(5);
        await rig.Coordinator.NotifyWakeAsync();
        Assert.Equal([DownloadAction.Pause, DownloadAction.Resume], rig.Engine.Commands.Select(row => row.Action));
        Assert.False(rig.Repository.Entries[entry.Job.Id].PolicyHeld);
    }

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
    public async Task Bandwidth_intent_is_durable_before_rpc_and_confirmed_for_the_observed_generation()
    {
        var rig = new Rig();
        var profile = new BandwidthProfile("Evening", new(262144), new(65536));
        rig.Engine.BeforeBandwidth = applied =>
        {
            Assert.Equal(profile, applied);
            Assert.Equal(1, rig.Repository.Bandwidth.RequestedRevision);
            Assert.Equal(OperationStatus.Pending, rig.Repository.Bandwidth.RequestStatus);
        };
        await rig.Coordinator.SetBandwidthProfileAsync(profile);
        Assert.Equal(profile, rig.Repository.Bandwidth.Confirmed);
        Assert.Equal(1, rig.Repository.Bandwidth.ConfirmedRevision);
        Assert.Equal(rig.Engine.Generation, rig.Repository.Bandwidth.ConfirmedGeneration);
        Assert.Null(rig.Repository.Bandwidth.RequestStatus);
    }

    [Fact]
    public async Task Lost_bandwidth_reply_remains_uncertain_until_observation_without_duplicate_mutation()
    {
        var rig = new Rig();
        var profile = new BandwidthProfile("Slow", new(131072), ByteRateLimit.Unlimited);
        rig.Engine.BeforeBandwidth = _ => throw new EngineException(EngineFailure.RpcUnavailable, "Lost reply.");
        await Assert.ThrowsAsync<EngineException>(() => rig.Coordinator.SetBandwidthProfileAsync(profile));
        Assert.Equal(OperationStatus.Uncertain, rig.Repository.Bandwidth.RequestStatus);
        var reopened = rig.Create(new DownloadRuntimeStore());
        await reopened.RefreshAsync();
        Assert.Equal(1, rig.Engine.BandwidthCalls);
        Assert.Equal(OperationStatus.Uncertain, rig.Repository.Bandwidth.RequestStatus);
        rig.Engine.DownloadCap = profile.Download;
        await reopened.RefreshAsync();
        Assert.Null(rig.Repository.Bandwidth.RequestStatus);
        Assert.Equal(profile, rig.Repository.Bandwidth.Confirmed);
        Assert.Equal(1, rig.Engine.BandwidthCalls);
    }

    [Fact]
    public async Task Invalid_bandwidth_rate_never_persists_or_calls_engine()
    {
        var rig = new Rig();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.Coordinator.SetBandwidthProfileAsync(
            new("Invalid", new(0), ByteRateLimit.Unlimited)));
        Assert.Equal(0, rig.Repository.Bandwidth.RequestedRevision);
        Assert.Equal(0, rig.Engine.BandwidthCalls);
    }

    [Fact]
    public async Task Rejected_bandwidth_rate_is_visible_and_next_request_can_replace_it()
    {
        var rig = new Rig();
        rig.Engine.BeforeBandwidth = _ => throw new EngineException(EngineFailure.RequestRejected, "Rejected.");
        await Assert.ThrowsAsync<EngineException>(() => rig.Coordinator.SetBandwidthProfileAsync(
            new("Rejected", new(65536), ByteRateLimit.Unlimited)));
        Assert.Equal(OperationStatus.Failed, rig.Repository.Bandwidth.RequestStatus);
        Assert.Equal(0, rig.Repository.Bandwidth.ConfirmedRevision);
        rig.Engine.BeforeBandwidth = null;
        await rig.Coordinator.SetBandwidthProfileAsync(new("Accepted", new(131072), ByteRateLimit.Unlimited));
        Assert.Equal(2, rig.Repository.Bandwidth.ConfirmedRevision);
        Assert.Null(rig.Repository.Bandwidth.RequestStatus);
    }

    [Fact]
    public async Task Bandwidth_editor_reports_rejection_without_claiming_the_profile_is_active()
    {
        var rig = new Rig();
        rig.Engine.BeforeBandwidth = _ => throw new EngineException(EngineFailure.RequestRejected, "Rejected.");
        await Assert.ThrowsAsync<EngineException>(() => rig.Coordinator.SetBandwidthProfileAsync(
            new("Rejected", new(65536), ByteRateLimit.Unlimited)));
        using var view = new DownloadsViewModel(rig.Coordinator, rig.Store, new ImmediateDispatcher());
        Assert.Contains("Rejected was rejected", view.BandwidthMessage);
        Assert.Contains("last confirmed profile: Unlimited", view.BandwidthMessage);
        Assert.DoesNotContain("Active profile", view.BandwidthMessage);
    }

    [Fact]
    public async Task Saved_profiles_survive_schedule_changes_without_applying_engine_options()
    {
        var rig = new Rig();
        var profiles = new[] { new BandwidthProfile("Metered", new(32768), ByteRateLimit.Unlimited),
            new BandwidthProfile("Fast", new(262144), new(65536)) };
        await rig.Coordinator.SaveBandwidthProfilesAsync(profiles);
        Assert.Equal(0, rig.Engine.BandwidthCalls);
        await rig.Coordinator.SetBandwidthScheduleAsync(new("UTC", BandwidthProfile.Unlimited,
            [new(DayOfWeek.Monday, 480, 540, ScheduleAction.Block)], Enabled: false));
        Assert.Equal(profiles, rig.Repository.Schedule.SavedProfiles);
        var reopenedStore = new DownloadRuntimeStore();
        await rig.Create(reopenedStore).InitializeAsync(default);
        Assert.Equal(profiles, reopenedStore.Current.Schedule?.SavedProfiles);
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Coordinator.SaveBandwidthProfilesAsync([profiles[0], profiles[0]]));
        Assert.Equal(profiles, rig.Repository.Schedule.SavedProfiles);
    }

    [Fact]
    public async Task Bandwidth_preferences_edit_multiple_intervals_and_selected_job_cap()
    {
        var rig = new Rig(); await rig.Add(); await rig.Coordinator.RefreshAsync();
        using var view = new DownloadsViewModel(rig.Coordinator, rig.Store, new ImmediateDispatcher());
        view.BandwidthProfileName = "Metered"; view.DownloadLimitKib = 32;
        await view.SaveProfileCommand.ExecuteAsync(null);
        Assert.Single(view.SavedProfiles);
        view.ScheduleTimeZoneId = "UTC";
        view.ScheduleDay = (int)DayOfWeek.Monday; view.ScheduleStartTime = "08:30"; view.ScheduleEndTime = "10:15";
        await view.AddRuleCommand.ExecuteAsync(null);
        view.ScheduleDay = (int)DayOfWeek.Tuesday; view.ScheduleStartTime = "22:00"; view.ScheduleEndTime = "01:00";
        view.ScheduleBlocks = true;
        await view.AddRuleCommand.ExecuteAsync(null);
        view.ScheduleEnabled = true;
        await view.ApplyScheduleCommand.ExecuteAsync(null);
        Assert.Equal(2, rig.Repository.Schedule.Intervals.Count);
        Assert.Equal(510, rig.Repository.Schedule.Intervals[0].StartMinute);
        Assert.Equal(1500, rig.Repository.Schedule.Intervals[1].EndMinute);
        Assert.Single(rig.Repository.Schedule.SavedProfiles!);
        view.ScheduleStartTime = "23:00"; view.ScheduleEndTime = "23:30";
        await view.AddRuleCommand.ExecuteAsync(null);
        await view.ApplyScheduleCommand.ExecuteAsync(null);
        Assert.Contains("overlap", view.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, rig.Repository.Schedule.Intervals.Count);
        view.RemoveRuleCommand.Execute(null);
        view.SelectedSavedProfileIndex = 0;
        Assert.Equal("Metered", view.BandwidthProfileName);
        view.SelectedBandwidthJob = view.Downloads[0]; view.JobLimitKib = 16;
        await view.ApplyJobBandwidthCommand.ExecuteAsync(null);
        Assert.Equal(16384, rig.Repository.JobBandwidth[view.Downloads[0].Id].Confirmed.BytesPerSecond);
        Assert.Contains("16 KiB/s", view.JobBandwidthMessage);
    }

    [Fact]
    public async Task Same_generation_bandwidth_drift_becomes_uncertain_without_reissuing_a_command()
    {
        var rig = new Rig();
        var profile = new BandwidthProfile("Stable", new(131072), ByteRateLimit.Unlimited);
        await rig.Coordinator.SetBandwidthProfileAsync(profile);
        Assert.Null(rig.Repository.Bandwidth.RequestStatus);
        rig.Engine.DownloadCap = new(262144);
        await rig.Coordinator.RefreshAsync();
        Assert.Equal(OperationStatus.Uncertain, rig.Repository.Bandwidth.RequestStatus);
        Assert.Equal(profile, rig.Repository.Bandwidth.Confirmed);
        Assert.Equal(1, rig.Engine.BandwidthCalls);
        await rig.Coordinator.RefreshAsync();
        Assert.Equal(1, rig.Engine.BandwidthCalls);
    }

    [Fact]
    public async Task Job_cap_intent_is_durable_before_owned_rpc_and_uncertain_reply_is_reconciled()
    {
        var rig = new Rig(); var entry = await rig.Add("job-cap.bin");
        rig.Engine.BeforeJobBandwidth = (jobId, gid, cap) =>
        {
            Assert.Equal(entry.Job.Id, jobId);
            Assert.Equal(entry.Attempt.EngineId, gid);
            Assert.Equal(65536, cap.BytesPerSecond);
            Assert.Equal(OperationStatus.Pending, rig.Repository.JobBandwidth[jobId].RequestStatus);
            throw new EngineException(EngineFailure.RpcUnavailable, "Lost reply.");
        };
        await Assert.ThrowsAsync<EngineException>(() => rig.Coordinator.SetJobBandwidthAsync(entry.Job.Id, new(65536)));
        Assert.Equal(OperationStatus.Uncertain, rig.Repository.JobBandwidth[entry.Job.Id].RequestStatus);
        rig.Engine.JobCaps[entry.Attempt.EngineId] = new(65536);
        await rig.Coordinator.RefreshAsync();
        Assert.Null(rig.Repository.JobBandwidth[entry.Job.Id].RequestStatus);
        Assert.Equal(1, rig.Engine.JobBandwidthCalls);
    }

    [Fact]
    public async Task Per_job_drift_does_not_reissue_or_change_other_jobs()
    {
        var rig = new Rig(); var first = await rig.Add("first-cap.bin"); var second = await rig.Add("second-cap.bin");
        await rig.Coordinator.SetJobBandwidthAsync(first.Job.Id, new(131072));
        rig.Engine.JobCaps[first.Attempt.EngineId] = new(262144);
        await rig.Coordinator.RefreshAsync();
        Assert.Equal(OperationStatus.Uncertain, rig.Repository.JobBandwidth[first.Job.Id].RequestStatus);
        Assert.False(rig.Repository.JobBandwidth.ContainsKey(second.Job.Id));
        Assert.Equal(1, rig.Engine.JobBandwidthCalls);
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
    private sealed class ScheduleClock(DateTimeOffset initial) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = initial;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class ImmediateDispatcher : IUiDispatcher { public void Enqueue(Action action) => action(); }
    private sealed class Rig
    {
        public MemoryDownloadRepository Repository { get; } = new();
        public CommandEngine Engine { get; } = new();
        public DownloadRuntimeStore Store { get; } = new();
        public DownloadCoordinator Coordinator { get; }
        private readonly TimeProvider _clock;
        public Rig(TimeProvider? clock = null) { _clock = clock ?? TimeProvider.System; Coordinator = Create(Store); }
        public DownloadCoordinator Create(DownloadRuntimeStore store) => new(Engine, store, _clock, Repository, new TestDownloadDestination());
        public async Task<DownloadEntry> Add(string name = "file.bin")
        { await Coordinator.AddAsync("https://example.com/" + name); return Store.Current.Downloads.Last(); }
    }
    private sealed class CommandEngine : IAria2Client
    {
        public bool SupportsChecksumVerification => ChecksumEnabled;
        public bool ChecksumEnabled = true;
        public Dictionary<string, EngineDownload> Transfers { get; } = [];
        public List<(DownloadAction Action, int? Position)> Commands { get; } = [];
        public Func<string, DownloadAction, Task>? BeforeExecute;
        public Func<Task>? BeforeAdd, DelayNextObservation;
        public Action<int>? BeforeLimit;
        public Action<BandwidthProfile>? BeforeBandwidth;
        public Action<Guid, string, ByteRateLimit>? BeforeJobBandwidth;
        public bool LoseReply, CompleteInstead;
        public int AddCount, LimitCalls, Limit = 3;
        public int BandwidthCalls;
        public int JobBandwidthCalls;
        public ByteRateLimit DownloadCap = ByteRateLimit.Unlimited, UploadCap = ByteRateLimit.Unlimited;
        public Dictionary<string, ByteRateLimit> JobCaps { get; } = [];
        public EngineDownloadRequest? LastAddedRequest { get; private set; }
        public string Generation = "initial-session";
        public DownloadStatus InitialStatus = DownloadStatus.Downloading;
        public async Task<EngineDownload> AddAsync(EngineDownloadRequest request, CancellationToken token)
        {
            LastAddedRequest = request;
            AddCount++; if (BeforeAdd is not null) await BeforeAdd();
            var download = new EngineDownload(request.EngineId, new(request.StartPaused ? DownloadStatus.Paused : InitialStatus, 10, 1000, 20));
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
        public Task<BandwidthObservation> ApplyBandwidthAsync(BandwidthProfile profile, CancellationToken token)
        {
            BandwidthCalls++; BeforeBandwidth?.Invoke(profile);
            DownloadCap = profile.Download; UploadCap = profile.Upload;
            return Task.FromResult(new BandwidthObservation(Generation, DownloadCap, UploadCap));
        }
        public Task<BandwidthObservation> ObserveBandwidthAsync(CancellationToken token) =>
            Task.FromResult(new BandwidthObservation(Generation, DownloadCap, UploadCap));
        public Task<JobBandwidthObservation> ApplyJobBandwidthAsync(Guid jobId, string gid, ByteRateLimit cap, CancellationToken token)
        {
            JobBandwidthCalls++; BeforeJobBandwidth?.Invoke(jobId, gid, cap);
            JobCaps[gid] = cap;
            return Task.FromResult(new JobBandwidthObservation(Generation, gid, cap));
        }
        public Task<JobBandwidthObservation> ObserveJobBandwidthAsync(Guid jobId, string gid, CancellationToken token) =>
            Task.FromResult(new JobBandwidthObservation(Generation, gid, JobCaps.GetValueOrDefault(gid)));
    }
}
