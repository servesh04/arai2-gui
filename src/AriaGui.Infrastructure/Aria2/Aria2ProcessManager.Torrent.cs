using System.Security.Cryptography;
using AriaGui.Application.Engine;
using AriaGui.Application.Downloads;
using AriaGui.Core.Downloads;
using AriaGui.Infrastructure.Downloads;
using AriaGui.Infrastructure.Persistence;

namespace AriaGui.Infrastructure.Aria2;

public sealed partial class Aria2ProcessManager
{
    public async Task<Guid> CreateTorrentDraftAsync(byte[] metainfo, IEnumerable<int> fileIndices,
        string destinationRoot, bool privacyNoticeAccepted, CancellationToken token)
    {
        if (!privacyNoticeAccepted)
            throw new EngineException(EngineFailure.RequestRejected, "Review the torrent IP exposure and firewall notice before adding a torrent.");
        var repository = _torrentRepository ?? throw new EngineException(EngineFailure.RequestRejected, "Torrent storage is unavailable.");
        var payloadRoot = Path.Combine(_options.RuntimeDirectory, "torrent-payloads", Guid.NewGuid().ToString("N"));
        return await repository.CreateDraftAsync(metainfo, fileIndices, payloadRoot, destinationRoot, token).ConfigureAwait(false);
    }

    public async Task<TorrentEngineObservation> DispatchTorrentAsync(Guid jobId, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var rpc = ConnectedClient();
            var info = await CheckHealthCoreAsync(token).ConfigureAwait(false);
            return await DispatchTorrentCoreAsync(rpc, info.SessionId, jobId, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<TorrentEngineObservation> RetryFailedTorrentAsync(Guid jobId, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var repository = _torrentRepository ?? throw new EngineException(EngineFailure.RequestRejected, "Torrent storage is unavailable.");
            var draft = await repository.LoadDraftAsync(jobId, token).ConfigureAwait(false)
                ?? throw new EngineException(EngineFailure.TransferNotFound, "Torrent record is missing.");
            if (draft.Stage != TorrentStage.Failed)
                throw new EngineException(EngineFailure.RequestRejected, "Only a failed torrent can be retried.");
            var binding = await repository.LoadBindingAsync(jobId, token).ConfigureAwait(false)
                ?? throw new EngineException(EngineFailure.TransferNotFound, "Failed torrent engine identity is missing.");
            var rpc = ConnectedClient();
            var info = await CheckHealthCoreAsync(token).ConfigureAwait(false);
            var observed = await rpc.GetTorrentAsync(binding.EngineId, draft.Manifest, draft.Selection, draft.PayloadRoot, token).ConfigureAwait(false);
            if (observed?.Stage == TorrentStage.Publishing)
            {
                await PublishTorrentCoreAsync(repository, draft, observed, token).ConfigureAwait(false);
                return observed with { Stage = TorrentStage.Completed };
            }
            if (observed is not null && observed.Stage != TorrentStage.Failed)
                throw new EngineException(EngineFailure.RequestRejected, "Torrent engine state changed; refresh it before retrying.");
            if (observed is not null) await rpc.RemoveFailedTorrentResultAsync(binding.EngineId, token).ConfigureAwait(false);
            return await DispatchTorrentCoreAsync(rpc, info.SessionId, jobId, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<TorrentEngineObservation?> ObserveTorrentAsync(Guid jobId, CancellationToken token,
        IProgress<TorrentPublicationProgress>? publicationProgress = null)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var repository = _torrentRepository ?? throw new EngineException(EngineFailure.RequestRejected, "Torrent storage is unavailable.");
            var draft = await repository.LoadDraftAsync(jobId, token).ConfigureAwait(false);
            var binding = await repository.LoadBindingAsync(jobId, token).ConfigureAwait(false);
            if (draft is null || binding is null) return null;
            var rpc = ConnectedClient();
            var observed = await rpc.GetTorrentAsync(binding.EngineId, draft.Manifest, draft.Selection, draft.PayloadRoot, token).ConfigureAwait(false);
            if (observed?.Stage == TorrentStage.Publishing)
            {
                await PublishTorrentCoreAsync(repository, draft, observed, token, publicationProgress).ConfigureAwait(false);
                return observed with { Stage = TorrentStage.Completed };
            }
            if (observed is not null && observed.Stage is not TorrentStage.Stopped)
                await repository.SetStageAsync(jobId, observed.Stage, token).ConfigureAwait(false);
            return observed;
        }
        finally { _gate.Release(); }
    }

    private async Task<TorrentEngineObservation> DispatchTorrentCoreAsync(Aria2RpcClient rpc, string generation,
        Guid jobId, CancellationToken token)
    {
        var repository = _torrentRepository ?? throw new EngineException(EngineFailure.RequestRejected, "Torrent storage is unavailable.");
        var draft = await repository.LoadDraftAsync(jobId, token).ConfigureAwait(false)
            ?? throw new EngineException(EngineFailure.RequestRejected, "Torrent draft is missing.");
        CheckTorrentSpaceAndRoots(draft);
        var previous = await repository.LoadBindingAsync(jobId, token).ConfigureAwait(false);
        var gid = previous?.EngineId ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        await repository.PrepareDispatchAsync(jobId, generation, gid, token).ConfigureAwait(false);
        var current = await rpc.GetTorrentAsync(gid, draft.Manifest, draft.Selection, draft.PayloadRoot, token).ConfigureAwait(false);
        if (current is null)
        {
            UserRuntimeDirectory.EnsurePrivate(draft.PayloadRoot);
            current = await rpc.AddTorrentAsync(draft.Metainfo, draft.Manifest, draft.Selection,
                draft.PayloadRoot, gid, token).ConfigureAwait(false);
        }
        _knownIds.Add(gid);
        await rpc.SaveSessionAsync(token).ConfigureAwait(false);
        if (current.Stage == TorrentStage.Publishing)
        {
            await PublishTorrentCoreAsync(repository, draft, current, token).ConfigureAwait(false);
            return current with { Stage = TorrentStage.Completed };
        }
        if (current.Stage == TorrentStage.Stopped)
        {
            await rpc.ExecuteAsync(gid, DownloadAction.Resume, null, token).ConfigureAwait(false);
            current = await rpc.GetTorrentAsync(gid, draft.Manifest, draft.Selection, draft.PayloadRoot, token).ConfigureAwait(false)
                ?? throw new EngineException(EngineFailure.TransferNotFound, "Torrent disappeared during dispatch.");
            await rpc.SaveSessionAsync(token).ConfigureAwait(false);
        }
        await repository.SetStageAsync(jobId, current.Stage, token).ConfigureAwait(false);
        return current;
    }

    private async Task RecoverTorrentsCoreAsync(Aria2RpcClient rpc, string generation, CancellationToken token)
    {
        if (_torrentRepository is null) return;
        foreach (var jobId in await _torrentRepository.LoadUnfinishedIdsAsync(token).ConfigureAwait(false))
        {
            try { await DispatchTorrentCoreAsync(rpc, generation, jobId, token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is EngineException or DownloadStorageException or IOException)
            {
                await _torrentRepository.SetStageAsync(jobId, TorrentStage.Failed, token).ConfigureAwait(false);
                _log.Write("recovery", $"Torrent {jobId:N} needs review; other transfers remain available.");
            }
        }
    }

    private static async Task PublishTorrentCoreAsync(SqliteTorrentMetadataRepository repository,
        StoredTorrentDraft draft, TorrentEngineObservation observed, CancellationToken token,
        IProgress<TorrentPublicationProgress>? progress = null)
    {
        try { await new TorrentPublisher(repository).PublishAsync(draft, observed, token, progress).ConfigureAwait(false); }
        catch (IOException)
        {
            await repository.SetStageAsync(draft.JobId, TorrentStage.Failed, token).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<IReadOnlySet<string>> VerifyTorrentSurvivorAsync(Aria2RpcClient rpc, CancellationToken token)
    {
        var verified = new HashSet<string>(StringComparer.Ordinal);
        if (_torrentRepository is null) return verified;
        foreach (var binding in await _torrentRepository.LoadBindingsAsync(token).ConfigureAwait(false))
        {
            var draft = await _torrentRepository.LoadDraftAsync(binding.JobId, token).ConfigureAwait(false);
            if (draft is null || binding.InfoHash != draft.Manifest.InfoHash)
                throw AdoptionRefused("Saved torrent identity is inconsistent; surviving engine was preserved.");
            if (await rpc.GetTorrentAsync(binding.EngineId, draft.Manifest, draft.Selection,
                draft.PayloadRoot, token).ConfigureAwait(false) is not null) verified.Add(binding.EngineId);
        }
        return verified;
    }

    private void CheckTorrentSpaceAndRoots(StoredTorrentDraft draft)
    {
        var privateParentRoot = Path.GetFullPath(Path.Combine(_options.RuntimeDirectory, "torrent-payloads"));
        UserRuntimeDirectory.EnsurePrivate(privateParentRoot);
        var privateParent = privateParentRoot + Path.DirectorySeparatorChar;
        var privateRoot = Path.GetFullPath(draft.PayloadRoot);
        var publicRoot = Path.GetFullPath(draft.DestinationRoot);
        if (!privateRoot.StartsWith(privateParent, StringComparison.OrdinalIgnoreCase)
            || !Path.IsPathFullyQualified(draft.DestinationRoot) || !Directory.Exists(publicRoot)
            || publicRoot.StartsWith(Path.GetFullPath(_options.RuntimeDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new EngineException(EngineFailure.RequestRejected, "Torrent payload or destination folder is unsafe.");
        var privateDrive = Path.GetPathRoot(privateRoot)!;
        var publicDrive = Path.GetPathRoot(publicRoot)!;
        var existingBytes = 0L;
        foreach (var file in draft.Manifest.Files)
        {
            var path = Path.GetFullPath(Path.Combine(privateRoot, file.RelativePath));
            if (!path.StartsWith(privateRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new EngineException(EngineFailure.RequestRejected, "Torrent file path escapes private storage.");
            if (ContainsReparse(privateRoot, path))
                throw new EngineException(EngineFailure.RequestRejected, "Torrent payload contains a linked path.");
            if (File.Exists(path))
            {
                existingBytes = checked(existingBytes + Math.Min(file.Length, new FileInfo(path).Length));
            }
        }
        var requiredPrivate = draft.Manifest.TotalBytes - existingBytes;
        var requiredPublic = draft.Selection.SelectedBytes;
        if (string.Equals(privateDrive, publicDrive, StringComparison.OrdinalIgnoreCase))
        {
            if (new DriveInfo(privateDrive).AvailableFreeSpace < checked(requiredPrivate + requiredPublic))
                throw new EngineException(EngineFailure.StorageUnavailable, "Torrent needs space for both its private payload and published files.");
        }
        else if (new DriveInfo(privateDrive).AvailableFreeSpace < requiredPrivate
            || new DriveInfo(publicDrive).AvailableFreeSpace < requiredPublic)
            throw new EngineException(EngineFailure.StorageUnavailable, "Torrent payload or destination drive has too little free space.");
    }

    private static bool ContainsReparse(string root, string path)
    {
        var current = root;
        if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        foreach (var component in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, component);
            if ((Directory.Exists(current) || File.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        }
        return false;
    }
}
