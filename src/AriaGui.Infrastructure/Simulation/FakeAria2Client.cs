using AriaGui.Application.Downloads;
using AriaGui.Core.Downloads;

namespace AriaGui.Infrastructure.Simulation;

// Deterministic elapsed-time simulation: no network requests, processes, or files.
public sealed class FakeAria2Client(TimeProvider clock) : IAria2Client
{
    private const long MiB = 1024 * 1024;
    private readonly Dictionary<string, SimulatedTransfer> _transfers = [];
    private readonly object _sync = new();

    public Task<EngineDownload> AddAsync(EngineDownloadRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_transfers.ContainsKey(request.EngineId))
                throw new InvalidOperationException("A transfer with this engine ID already exists.");
            var transfer = new SimulatedTransfer(request, clock.GetUtcNow(), 240 * MiB);
            _transfers.Add(request.EngineId, transfer);
            return Task.FromResult(Observe(transfer));
        }
    }

    public Task<IReadOnlyList<EngineDownload>> GetDownloadsAsync(IReadOnlyList<string> engineIds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
            return Task.FromResult<IReadOnlyList<EngineDownload>>(_transfers.Values.Where(item => engineIds.Contains(item.Request.EngineId)).Select(Observe).ToArray());
    }

    private EngineDownload Observe(SimulatedTransfer transfer)
    {
        var seconds = Math.Max(0, (clock.GetUtcNow() - transfer.StartedAt).TotalSeconds);
        var rate = 2 * MiB;
        var received = (long)Math.Min(transfer.TotalBytes, Math.Max(0, seconds - 1) * rate);
        var status = seconds < 1 ? DownloadStatus.Queued
            : received == transfer.TotalBytes ? DownloadStatus.Completed : DownloadStatus.Downloading;
        long? total = transfer.Request.SimulateUnknownSize && seconds < 12 ? null : transfer.TotalBytes;
        return new(transfer.Request.EngineId, new(status, received, total, rate));
    }

    private sealed record SimulatedTransfer(EngineDownloadRequest Request, DateTimeOffset StartedAt, long TotalBytes);
}
