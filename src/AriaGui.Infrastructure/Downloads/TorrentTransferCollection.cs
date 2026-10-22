using AriaGui.Application.Downloads;
using AriaGui.Application.Engine;
using AriaGui.Core.Downloads;
using AriaGui.Infrastructure.Aria2;
using AriaGui.Infrastructure.Persistence;

namespace AriaGui.Infrastructure.Downloads;

/// <summary>Keeps the durable torrent records in the shared transfer projection.</summary>
public sealed class TorrentTransferCollection(
    SqliteTorrentMetadataRepository repository, IManagedAria2Engine managedEngine,
    DownloadRuntimeStore store) : IAsyncDisposable
{
    private readonly Aria2ProcessManager _engine = (Aria2ProcessManager)managedEngine;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _loop;
    private bool _initialized;

    public void Start()
    {
        if (_loop is not null) return;
        _engine.TorrentPublicationProgressChanged += PublicationProgressChanged;
        _loop = RunAsync(_lifetime.Token);
    }

    private void PublicationProgressChanged(object? sender, TorrentPublicationProgress progress)
    {
        var rows = store.Current.Torrents;
        if (!rows.Any(row => row.Job.Id == progress.JobId)) return;
        store.PublishTorrents(rows.Select(row => row.Job.Id == progress.JobId
            ? TorrentTransferProjection.Publishing(row, progress.PublishedBytes, progress.TotalBytes) : row));
    }

    public async Task RefreshAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!_initialized) { await repository.InitializeAsync(token).ConfigureAwait(false); _initialized = true; }
            var rows = new List<DownloadEntry>();
            foreach (var id in await repository.LoadDraftIdsAsync(token).ConfigureAwait(false))
            {
                var draft = await repository.LoadDraftAsync(id, token).ConfigureAwait(false);
                if (draft is null) continue;
                TorrentEngineObservation? observed = null;
                if (draft.Stage is not (TorrentStage.AwaitingSelection or TorrentStage.Completed))
                {
                    try { observed = await _engine.ObserveTorrentAsync(id, token).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is EngineException or DownloadStorageException or IOException)
                    {
                        // The durable row remains visible while the engine reconnects.
                    }
                    draft = await repository.LoadDraftAsync(id, token).ConfigureAwait(false) ?? draft;
                }
                var stage = draft.Stage == TorrentStage.Stopped ? TorrentStage.Stopped : observed?.Stage ?? draft.Stage;
                var state = new TorrentTransferState(id, draft.Manifest.Name, draft.Manifest.InfoHash,
                    draft.DestinationRoot, draft.CreatedAt, stage, draft.Manifest.TotalBytes,
                    draft.Selection.SelectedBytes, observed?.CompletedBytes ?? 0,
                    observed?.DownloadBytesPerSecond ?? 0, draft.Manifest.Files.Count,
                    draft.Selection.FileIndices.Count,
                    observed?.Error ?? (draft.Stage == TorrentStage.Failed ? "Torrent transfer failed; review the torrent and retry." : null),
                    Peers: observed?.Peers, Seeds: observed?.Seeds,
                    UploadBytesPerSecond: observed?.UploadBytesPerSecond,
                    Ratio: observed?.UploadedBytes is long uploaded && observed.CompletedBytes > 0
                        ? uploaded / (double)observed.CompletedBytes : null,
                    IsPaused: draft.Stage == TorrentStage.Stopped,
                    VerifiedBytes: observed?.Stage == TorrentStage.Checking ? observed.VerifiedBytes : null);
                rows.Add(TorrentTransferProjection.Map(state));
            }
            store.PublishTorrents(rows);
        }
        finally { _gate.Release(); }
    }

    private async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            try { await RefreshAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is DownloadStorageException or IOException or EngineException)
            {
                // Keep the last published rows and retry when storage or the engine returns.
            }
        } while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
    }

    public async ValueTask DisposeAsync()
    {
        _engine.TorrentPublicationProgressChanged -= PublicationProgressChanged;
        _lifetime.Cancel();
        if (_loop is not null)
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        _lifetime.Dispose();
        _gate.Dispose();
    }
}
