using AriaGui.Application.Engine;
using AriaGui.Core.Downloads;

namespace AriaGui.Application.Downloads;

public sealed partial class DownloadCoordinator
{
    public async Task SetBandwidthProfileAsync(BandwidthProfile profile, CancellationToken token = default)
    {
        profile.Validate();
        await InitializeAsync(token).ConfigureAwait(false);
        await _bandwidthGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            BandwidthPolicy intent;
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_bandwidth.RequestStatus is OperationStatus.Pending or OperationStatus.Uncertain)
                    throw new DownloadRequestException("Wait for the previous bandwidth setting to be reconciled.");
                intent = _bandwidth with { RequestedRevision = checked(_bandwidth.RequestedRevision + 1),
                    Requested = profile, RequestStatus = OperationStatus.Pending };
                await repository.SaveBandwidthPolicyAsync(intent, token).ConfigureAwait(false);
                _bandwidth = intent; _epoch++;
                store.Publish(store.Current.Downloads, store.Current.Connection, bandwidthPolicy: intent);
            }
            finally { _gate.Release(); }

            try
            {
                var observed = await engine.ApplyBandwidthAsync(profile, token).ConfigureAwait(false);
                if (observed.Download != profile.Download || observed.Upload != profile.Upload)
                    throw new EngineException(EngineFailure.InvalidResponse, "The bandwidth setting was not confirmed.");
                await SaveBandwidthAsync(intent with { ConfirmedRevision = intent.RequestedRevision, Confirmed = profile,
                    ConfirmedGeneration = observed.Generation, RequestStatus = null }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var status = ex is EngineException { Failure: EngineFailure.RequestRejected }
                    ? OperationStatus.Failed : OperationStatus.Uncertain;
                await SaveBandwidthAsync(intent with { RequestStatus = status }).ConfigureAwait(false);
                throw;
            }
        }
        finally { _bandwidthGate.Release(); }
    }

    private async Task SaveBandwidthAsync(BandwidthPolicy policy)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // A concurrent poll may already have confirmed the same revision.
            if (_bandwidth.RequestedRevision != policy.RequestedRevision ||
                _bandwidth.ConfirmedRevision > policy.ConfirmedRevision) return;
            await repository.SaveBandwidthPolicyAsync(policy, CancellationToken.None).ConfigureAwait(false);
            _bandwidth = policy; _epoch++;
            store.Publish(store.Current.Downloads, store.Current.Connection, bandwidthPolicy: policy);
        }
        finally { _gate.Release(); }
    }

    public async Task SetJobBandwidthAsync(Guid jobId, ByteRateLimit cap, CancellationToken token = default)
    {
        cap.Validate();
        await InitializeAsync(token).ConfigureAwait(false);
        var jobGate = _jobGates.GetOrAdd(jobId, _ => new SemaphoreSlim(1, 1));
        await jobGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            JobBandwidthPolicy intent;
            string gid;
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var entry = store.Current.Downloads.SingleOrDefault(row => row.Job.Id == jobId);
                if (entry is null || IsTerminal(entry.Progress.Status) || entry.DesiredRemoved || _inFlight.Contains(jobId))
                    throw new DownloadRequestException("Choose an active owned job for a bandwidth cap.");
                gid = entry.Attempt.EngineId;
                var current = _jobBandwidth.GetValueOrDefault(jobId) ?? JobBandwidthPolicy.Default(jobId);
                if (current.RequestStatus is OperationStatus.Pending or OperationStatus.Uncertain)
                    throw new DownloadRequestException("Wait for the previous job bandwidth setting to be reconciled.");
                intent = current with { RequestedRevision = checked(current.RequestedRevision + 1),
                    Requested = cap, RequestStatus = OperationStatus.Pending };
                await repository.SaveJobBandwidthPolicyAsync(intent, token).ConfigureAwait(false);
                _jobBandwidth[jobId] = intent; _epoch++;
                store.Publish(store.Current.Downloads, store.Current.Connection,
                    jobBandwidthPolicies: new Dictionary<Guid, JobBandwidthPolicy>(_jobBandwidth));
            }
            finally { _gate.Release(); }

            try
            {
                var observed = await engine.ApplyJobBandwidthAsync(jobId, gid, cap, token).ConfigureAwait(false);
                if (observed.EngineId != gid || observed.Download != cap)
                    throw new EngineException(EngineFailure.InvalidResponse, "The job bandwidth setting was not confirmed.");
                await SaveJobBandwidthAsync(intent with { ConfirmedRevision = intent.RequestedRevision, Confirmed = cap,
                    ConfirmedGeneration = observed.Generation, RequestStatus = null }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var status = ex is EngineException { Failure: EngineFailure.RequestRejected }
                    ? OperationStatus.Failed : OperationStatus.Uncertain;
                await SaveJobBandwidthAsync(intent with { RequestStatus = status }).ConfigureAwait(false);
                throw;
            }
        }
        finally { jobGate.Release(); }
    }

    private async Task SaveJobBandwidthAsync(JobBandwidthPolicy policy)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_jobBandwidth.TryGetValue(policy.JobId, out var current) ||
                current.RequestedRevision != policy.RequestedRevision || current.ConfirmedRevision > policy.ConfirmedRevision) return;
            await repository.SaveJobBandwidthPolicyAsync(policy, CancellationToken.None).ConfigureAwait(false);
            _jobBandwidth[policy.JobId] = policy; _epoch++;
            store.Publish(store.Current.Downloads, store.Current.Connection,
                jobBandwidthPolicies: new Dictionary<Guid, JobBandwidthPolicy>(_jobBandwidth));
        }
        finally { _gate.Release(); }
    }
}
