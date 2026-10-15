using System.Security.Cryptography;
using System.Text.Json;
using AriaGui.Application.Downloads;
using AriaGui.Infrastructure.Aria2;
using AriaGui.Core.Downloads;

namespace AriaGui.Infrastructure.Downloads;

// The engine writes private working copies. Retained user payload/control remain
// immutable on denied continuation; publication has a durable, replayable journal.
internal sealed class ContinuityTransferLedger : IDisposable
{
    internal sealed record State(int Version, Guid Job, Guid Attempt, string Gid, string Source, string Target, string Work,
        string? Baseline, string? TargetHash, string? ControlHash, string Phase, string? NewHash, long Created, long CapturedLength = -1, bool RequirePublicOrigin = true,
        string? TargetDirectoryIdentity = null, string? LegacyParentGid = null, string? StageDirectoryIdentity = null, string? WorkDirectoryIdentity = null,
        string? WorkFileName = null, string? StageBaseDirectory = null);
    private readonly string _root;
    private readonly ContinuityLoopbackGateway _gateway;
    private readonly Action<string>? _fault;
    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PublicationDirectories> _leases = new(StringComparer.Ordinal);
    internal ContinuityTransferLedger(string runtime, ContinuityLoopbackGateway gateway, Action<string>? fault = null)
    { _root = Path.Combine(Path.GetFullPath(runtime), "continuity", "transfers"); UserRuntimeDirectory.EnsurePrivate(_root); _gateway = gateway; _fault = fault; }
    public async Task LoadAsync(CancellationToken token)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(_root, "*.json").Take(4097))
            {
                if (_states.Count >= 4096 || new FileInfo(path).Length > 65536 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Invalid transfer journal.");
                var state = JsonSerializer.Deserialize<State>(await File.ReadAllTextAsync(path, token)) ?? throw new IOException("Invalid transfer journal.");
                Validate(state); _states.Add(state.Gid, state);
                if (state.Phase == "complete-published") { Complete(state); continue; }
                HoldLease(state);
                if (state.Phase is "partial-prepared" or "complete-prepared") await RecoverPublicationAsync(state, token);
            }
        }
        catch { Dispose(); throw; }
    }
    private void HoldLease(State state)
    {
        if (_leases.ContainsKey(state.Gid)) return;
        ProtectedDestinationPolicy.Validate(Path.GetDirectoryName(state.Target)!);
        var lease = new PublicationDirectories(Path.GetDirectoryName(state.Target)!, StageRoot(state), state.Work, _root);
        try
        {
            if (state.TargetDirectoryIdentity != lease.Identity(Path.GetDirectoryName(state.Target)!)
                || state.StageDirectoryIdentity != lease.Identity(StageRoot(state)) || state.WorkDirectoryIdentity != lease.Identity(state.Work))
                throw new IOException("Transfer directory ownership changed.");
            _leases.Add(state.Gid, lease);
        }
        catch { lease.Dispose(); throw; }
    }
    public void Dispose() { foreach (var lease in _leases.Values) lease.Dispose(); _leases.Clear(); }
    private void Complete(State state)
    {
        _gateway.CompleteRoute(state.Attempt);
        if (_leases.Remove(state.Gid, out var lease)) lease.Dispose();
    }
    private static string StageRoot(State state) => Path.Combine(state.StageBaseDirectory ?? Path.GetDirectoryName(state.Target)!, ".aria-gui-continuity-" + state.Job.ToString("N"));
    private static string FileName(State state) => state.WorkFileName ?? Path.GetFileName(state.Target);
    private static void Validate(State state)
    {
        if (state.Version != 1 || state.Job == Guid.Empty || state.Attempt == Guid.Empty || state.Gid.Length != 16 || !state.Gid.All(Uri.IsHexDigit)
            || !Path.IsPathFullyQualified(state.Target) || !WindowsFileNamePolicy.IsValid(Path.GetFileName(state.Target))
            || !WindowsFileNamePolicy.IsValid(FileName(state)) || !Path.IsPathFullyQualified(state.Work)) throw new IOException("Invalid transfer identity.");
        if (state.StageBaseDirectory is not null && !IsDestinationAncestor(state.Target, state.StageBaseDirectory))
            throw new IOException("Invalid stage ancestor.");
        var root = Path.GetFullPath(StageRoot(state)) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(state.Work).StartsWith(root, StringComparison.OrdinalIgnoreCase)
            || state.Baseline is not null && !Path.GetFullPath(state.Baseline).StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid stage ownership.");
        if (state.Phase == "complete-published")
        {
            if (state.NewHash is not { Length: 64 } || !state.NewHash.All(Uri.IsHexDigit)) throw new IOException("Invalid publication receipt.");
            return; // Historical completion needs no access to a subsequently moved/deleted tree.
        }
        foreach (var path in new[] { StageRoot(state), state.Work, state.Baseline }.Where(path => path is not null))
            if (Directory.Exists(path) && (File.GetAttributes(path!) & FileAttributes.ReparsePoint) != 0) throw new IOException("Unsafe stage ownership.");
    }
    public async Task<EngineDownloadRequest> PrepareAsync(EngineDownloadRequest request, CancellationToken token, bool verifiedLegacyFresh = false)
    {
        if (request.ContinuityJobId is not Guid job || request.ContinuityAttemptId is not Guid attempt || job == Guid.Empty || attempt == Guid.Empty) throw new IOException("Durable transfer identity required.");
        if (_states.TryGetValue(request.EngineId, out var published) && published.Phase == "complete-published")
            throw new IOException("Published transfer cannot be dispatched again.");
        var source = request.ExactSourceUrl ?? request.Source.AbsoluteUri;
        var target = Path.GetFullPath(Path.Combine(request.Directory, request.FileName));
        ProtectedDestinationPolicy.Validate(Path.GetDirectoryName(target)!);
        // Until the private child exists, an empty destination ancestor could be
        // converted in place. Deny directory writes only during stage construction.
        using var parentDirectories = new PublicationDirectories(true, Path.GetDirectoryName(target)!, _root);
        if (_states.TryGetValue(request.EngineId, out var existing))
        {
            if (existing.TargetDirectoryIdentity is null || existing.TargetDirectoryIdentity != parentDirectories.Identity(Path.GetDirectoryName(target)!))
                throw new IOException("Destination directory ownership changed.");
            if (existing.Job != job || existing.Attempt != attempt || existing.Target != target || existing.Source != source) throw new IOException("Transfer journal conflict.");
            if (request.RequirePublicOrigin && !existing.RequirePublicOrigin) throw new IOException("Origin policy conflict.");
            HoldLease(existing);
            return Routed(request, existing);
        }
        var previous = _states.Values.Where(state => state.Job == job).OrderByDescending(state => state.Created).FirstOrDefault();
        if (previous is not null && (previous.Target != target || previous.Source != source)) throw new IOException("Job representation conflict.");
        if (previous is not null && (previous.TargetDirectoryIdentity is null || previous.TargetDirectoryIdentity != parentDirectories.Identity(Path.GetDirectoryName(target)!)))
            throw new IOException("Destination directory ownership changed.");
        var legacy = previous is null && (File.Exists(target) || File.Exists(target + ".aria2"));
        if (legacy && !verifiedLegacyFresh) throw new IOException("Legacy partial cannot authorize continuation.");
        if (previous is { Phase: "complete-published" }) throw new IOException("Completed transfer cannot be resubmitted.");
        var state = new State(1, job, attempt, request.EngineId, source, target, "", null, previous?.TargetHash, previous?.ControlHash, "working", null, DateTimeOffset.UtcNow.UtcTicks);
        // The pinned engine is not long-path aware. Private payload names need
        // not equal the eventual public name; retain the choice across retries
        // so control and snapshot ownership never changes beneath continuation.
        state = state with { StageBaseDirectory = previous is not null ? previous.StageBaseDirectory : SelectStageBase(state),
            WorkFileName = previous is not null ? previous.WorkFileName : null };
        if (previous is null && Path.Combine(StageRoot(state), attempt.ToString("N"), request.FileName + ".aria2").Length >= 240)
            state = state with { WorkFileName = "payload.bin" };
        state = state with { RequirePublicOrigin = request.RequirePublicOrigin || previous?.RequirePublicOrigin == true,
            TargetDirectoryIdentity = parentDirectories.Identity(Path.GetDirectoryName(target)!), LegacyParentGid = verifiedLegacyFresh ? request.LegacyParentEngineId : null };
        var root = StageRoot(state); UserRuntimeDirectory.EnsurePrivate(root);
        if (legacy)
        {
            // Explicit, reconciled retry starts afresh. The old pair is never a
            // representation record and never enters the new engine work directory.
            if (File.Exists(target)) { using var payload = new IdentifiedFile(target); state = state with { TargetHash = await payload.HashAsync(token) }; }
            if (File.Exists(target + ".aria2")) { using var control = new IdentifiedFile(target + ".aria2"); state = state with { ControlHash = await control.HashAsync(token) }; }
        }
        var retained = previous?.Baseline;
        if (retained is not null)
        {
            if (await _gateway.GetRepresentationAsync(job, token) is null) throw new IOException("Retained representation metadata missing.");
            var stage = await RetainedPartialStage.CreateAsync(root, Path.Combine(retained, FileName(state)), token);
            state = state with { Work = stage.Directory, Baseline = retained };
        }
        else
        {
            var work = Path.Combine(root, attempt.ToString("N")); Directory.CreateDirectory(work); state = state with { Work = work };
        }
        // Acquire child pins before releasing write denial, with no unprotected
        // gap. Journal renames need share-write, but pinned ancestors stay nonempty.
        using var stageDirectories = new PublicationDirectories(Path.GetDirectoryName(target)!, root, state.Work, _root);
        if (stageDirectories.Identity(Path.GetDirectoryName(target)!) != state.TargetDirectoryIdentity) throw new IOException("Destination directory ownership changed.");
        parentDirectories.Dispose();
        state = state with { StageDirectoryIdentity = stageDirectories.Identity(root), WorkDirectoryIdentity = stageDirectories.Identity(state.Work) };
        await _gateway.RegisterAsync(job, attempt, source, retained is not null, token, state.RequirePublicOrigin);
        await SaveAsync(state, token); HoldLease(state); return Routed(request, state);
    }
    private EngineDownloadRequest Routed(EngineDownloadRequest request, State state) => request with
    { Source = _gateway.GetAddress(state.Attempt), ExactSourceUrl = _gateway.GetAddress(state.Attempt).AbsoluteUri,
      Directory = state.Work, FileName = FileName(state), ContinueExisting = File.Exists(Path.Combine(state.Work, FileName(state)) + ".aria2") };
    private static string SelectStageBase(State state)
    {
        var ancestor = Path.GetDirectoryName(state.Target)!;
        while (Path.Combine(ancestor, ".aria-gui-continuity-" + state.Job.ToString("N"), state.Attempt.ToString("N"), "payload.bin.aria2").Length >= 240)
            ancestor = Path.GetDirectoryName(ancestor) ?? throw new IOException("No supported private stage path.");
        // Every ancestor is pinned root-first before this private child is made.
        // Nothing changes the ancestor ACL; only the new job-owned child is private.
        return ancestor;
    }
    private static bool IsDestinationAncestor(string target, string candidate)
    {
        if (!Path.IsPathFullyQualified(candidate) || candidate != Path.GetFullPath(candidate)) return false;
        for (var cursor = Path.GetDirectoryName(target); cursor is not null; cursor = Path.GetDirectoryName(cursor))
            if (string.Equals(cursor, candidate, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
    public bool Owns(string gid) => _states.ContainsKey(gid);
    public bool Owns(string gid, Guid jobId, Guid attemptId) =>
        _states.TryGetValue(gid, out var state) && state.Job == jobId && state.Attempt == attemptId && state.Phase != "complete-published";
    internal bool CanResume(string gid) => _states.TryGetValue(gid, out var state) && state.Phase != "complete-published";
    internal async Task<bool> VerifySurvivorAsync(Aria2RpcClient rpc, IReadOnlyList<DownloadEntry> history, CancellationToken token, IDownloadRepository? repository = null)
    {
        var inventory = await rpc.GetInventoryAsync(token, 4096);
        if (inventory.Count > 4096) return false;
        foreach (var gid in inventory)
        {
            if (!_states.TryGetValue(gid, out var state)) return false;
            Validate(state);
            if (state.Phase != "complete-published" && !HasDestinationIdentity(state)) return false;
            if (!_gateway.MatchesRoute(state.Job, state.Attempt, state.Source, state.RequirePublicOrigin)) return false;
            var entry = history.SingleOrDefault(row => row.Attempt.EngineId == gid);
            var historical = entry is null;
            if (historical && repository is not null) entry = await repository.GetAttemptAsync(gid, token);
            if (entry is null || entry.ExactSourceUrl is not null && !state.RequirePublicOrigin || state.Job != entry.Job.Id || state.Attempt != entry.Attempt.Id || state.Source != (entry.ExactSourceUrl ?? entry.Job.Source.AbsoluteUri)
                || !string.Equals(state.Target, entry.Job.Destination, StringComparison.OrdinalIgnoreCase)) return false;
            var observed = await rpc.FindAsync(gid, token);
            if (observed is null || historical && (!DownloadBrowsing.IsTerminal(entry.Progress.Status) || !DownloadBrowsing.IsTerminal(observed.Progress.Status))) return false;
            // The publication journal may precede the coordinator's Completed commit.
            // Authenticated engine completion plus the durable publication receipt
            // permits that crash window to converge; an active engine never does.
            if (state.Phase == "complete-published" && observed.Progress.Status != DownloadStatus.Completed) return false;
            var source = _gateway.GetAddress(state.Attempt).AbsoluteUri; var path = Path.Combine(state.Work, FileName(state));
            if (!(DownloadBrowsing.IsTerminal(observed.Progress.Status)
                ? await rpc.MatchesStoppedOwnershipAsync(gid, source, path, token)
                : await rpc.MatchesTransferOwnershipAsync(gid, source, path, token))) return false;
            if (await _gateway.GetRepresentationAsync(state.Job, state.Attempt, token) is { Resumable: false }
                && !DownloadBrowsing.IsTerminal(observed.Progress.Status)) return false;
            if (state.Phase != "complete-published" && observed.Progress.Status == DownloadStatus.Completed && await _gateway.GetRepresentationAsync(state.Job, state.Attempt, token) is { Resumable: false })
            {
                var receipt = await _gateway.GetCompletionAsync(state.Job, state.Attempt, token);
                var payload = state.Phase == "complete-published" ? state.Target : Path.Combine(state.Work, FileName(state));
                if (receipt is null || !File.Exists(payload) || new FileInfo(payload).Length != receipt.Length || await HashAsync(payload, token) != receipt.Sha256) return false;
            }
        }
        return true;
    }
    internal async Task<SessionRecoveryPlan> PrepareRestoreAsync(string runtime, IReadOnlyList<DownloadEntry> history, CancellationToken token)
    {
        var entries = new List<DownloadEntry>(); var lines = new List<string>(); var blocked = 0;
        var formatted = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var entry in history.Where(row => !row.DesiredRemoved && row.Progress.Status is not (DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Removed)))
        {
            if (!_states.TryGetValue(entry.Attempt.EngineId, out var state)) { blocked++; continue; }
            Validate(state);
            if (!HasDestinationIdentity(state)) { blocked++; continue; }
            if (!_gateway.MatchesRoute(state.Job, state.Attempt, state.Source, state.RequirePublicOrigin)) { blocked++; continue; }
            if (entry.ExactSourceUrl is not null && !state.RequirePublicOrigin || state.Job != entry.Job.Id || state.Attempt != entry.Attempt.Id || state.Source != (entry.ExactSourceUrl ?? entry.Job.Source.AbsoluteUri)
                || !string.Equals(state.Target, entry.Job.Destination, StringComparison.OrdinalIgnoreCase) || state.Phase == "complete-published") { blocked++; continue; }
            var work = Path.Combine(state.Work, FileName(state));
            var representation = await _gateway.GetRepresentationAsync(state.Job, state.Attempt, token);
            if (representation is { Resumable: false } || (File.Exists(work) || File.Exists(work + ".aria2")) && representation is not { Resumable: true }) { blocked++; continue; }
            if (state.Work.IndexOfAny(['\r', '\n']) >= 0) { blocked++; continue; }
            entries.Add(entry);
            formatted.Add(state.Gid, [_gateway.GetAddress(state.Attempt).AbsoluteUri, $" gid={state.Gid}", $" dir={state.Work}", $" out={FileName(state)}",
                " pause=true", $" continue={(File.Exists(work + ".aria2") ? "true" : "false")}", " always-resume=true", " allow-overwrite=false",
                " auto-file-renaming=false", " content-disposition=false", " follow-torrent=false", " follow-metalink=false", " split=1", " max-connection-per-server=1", ""]);
        }
        foreach (var entry in entries.Where(row => row.PendingOperation?.Action == DownloadAction.Move).ToArray())
        {
            entries.Remove(entry);
            entries.Insert(Math.Clamp(entry.PendingOperation!.QueuePosition ?? 0, 0, entries.Count), entry);
        }
        foreach (var entry in entries) lines.AddRange(formatted[entry.Attempt.EngineId]);
        string? input = null;
        if (entries.Count > 0)
        {
            var directory = Path.Combine(runtime, "sessions"); UserRuntimeDirectory.EnsurePrivate(directory);
            input = Path.Combine(directory, "restore-guarded-" + Guid.NewGuid().ToString("N") + ".txt");
            await using var file = new FileStream(input, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            await using var writer = new StreamWriter(file, new System.Text.UTF8Encoding(false), leaveOpen: true);
            await writer.WriteAsync(string.Join("\n", lines).AsMemory(), token); await writer.FlushAsync(token); file.Flush(true);
        }
        return new(entries, input, $"Restored {entries.Count} guarded transfers; {blocked} require review; direct session records preserved.");
    }
    public async Task<EngineDownload> ObserveAsync(EngineDownload download, CancellationToken token)
    {
        if (!_states.TryGetValue(download.EngineId, out var state)) return download with { Progress = new(DownloadStatus.Interrupted, download.Progress.DownloadedBytes, download.Progress.TotalBytes, 0), ErrorMessage = "Legacy transfer lacks continuity ownership." };
        if (state.Phase == "complete-published")
        {
            Validate(state);
            if (download.Progress.Status != DownloadStatus.Completed) throw new IOException("Published transfer cannot become active again.");
            return download; // Completion is historical; it does not assert the file still exists.
        }
        using var directories = new PublicationDirectories(Path.GetDirectoryName(state.Target)!, StageRoot(state), state.Work, _root);
        if (state.TargetDirectoryIdentity is null || directories.Identity(Path.GetDirectoryName(state.Target)!) != state.TargetDirectoryIdentity)
            throw new IOException("Destination directory ownership changed.");
        Validate(state);
        if (!_gateway.MatchesRoute(state.Job, state.Attempt, state.Source, state.RequirePublicOrigin)) throw new IOException("Transport ownership changed.");
        if (download.Progress.Status == DownloadStatus.Completed)
        {
            if (state.Phase != "complete-published")
            {
                var representation = await _gateway.GetRepresentationAsync(state.Job, state.Attempt, token);
                var receipt = await _gateway.GetCompletionAsync(state.Job, state.Attempt, token);
                var work = Path.Combine(state.Work, FileName(state));
                if (representation is null || !File.Exists(work)
                    || (representation.Resumable ? new FileInfo(work).Length != representation.Length
                        : receipt is null || new FileInfo(work).Length != receipt.Length || await HashAsync(work, token) != receipt.Sha256))
                    return download with { Progress = new(DownloadStatus.Failed, download.Progress.DownloadedBytes, download.Progress.TotalBytes, 0), ErrorMessage = "Completion could not validate the representation." };
                state = state with { Phase = "complete-prepared", NewHash = await HashAsync(work, token) };
                await SaveAsync(state, token); _fault?.Invoke("completion-prepared"); await RecoverPublicationAsync(state, token);
            }
        }
        else if (download.Progress.Status is DownloadStatus.Paused or DownloadStatus.Failed or DownloadStatus.Removed)
        {
            var work = Path.Combine(state.Work, FileName(state));
            if (File.Exists(work) && new FileInfo(work).Length > 0 && new FileInfo(work).Length != state.CapturedLength
                && File.Exists(work + ".aria2") && await _gateway.GetRepresentationAsync(state.Job, state.Attempt, token) is { Resumable: true })
            {
                var baseline = await RetainedPartialStage.CreateAsync(StageRoot(state), work, token);
                state = state with { Baseline = baseline.Directory, CapturedLength = new FileInfo(work).Length }; await SaveAsync(state, token);
                if (state.TargetHash is null)
                {
                    state = state with { Phase = "partial-prepared", TargetHash = await HashAsync(work, token), ControlHash = await HashAsync(work + ".aria2", token) };
                    await SaveAsync(state, token); await RecoverPublicationAsync(state, token);
                }
            }
        }
        return download;
    }
    private static bool HasDestinationIdentity(State state)
    {
        try
        {
            using var directories = new PublicationDirectories(Path.GetDirectoryName(state.Target)!);
            return state.TargetDirectoryIdentity is not null && state.TargetDirectoryIdentity == directories.Identity(Path.GetDirectoryName(state.Target)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    private async Task RecoverPublicationAsync(State state, CancellationToken token)
    {
        using var directories = new PublicationDirectories(Path.GetDirectoryName(state.Target)!, StageRoot(state), state.Work, _root);
        if (state.TargetDirectoryIdentity is null || directories.Identity(Path.GetDirectoryName(state.Target)!) != state.TargetDirectoryIdentity)
            throw new IOException("Destination directory ownership changed.");
        _fault?.Invoke("publication-directories-held");
        Validate(state); var name = FileName(state);
        if (state.Phase == "partial-prepared")
        {
            if (state.Baseline is null || state.TargetHash is null || state.ControlHash is null) throw new IOException("Incomplete partial journal.");
            await PublishNewCopyAsync(Path.Combine(state.Baseline, name), state.Target, state.TargetHash, token);
            await PublishNewCopyAsync(Path.Combine(state.Baseline, name + ".aria2"), state.Target + ".aria2", state.ControlHash, token);
            await SaveAsync(state with { Phase = "partial-published" }, token);
        }
        else if (state.Phase == "complete-prepared")
        {
            var work = Path.Combine(state.Work, name); if (state.NewHash is null) throw new IOException("Incomplete completion journal.");
            var backup = Path.Combine(StageRoot(state), "retained-" + state.Attempt.ToString("N") + ".payload");
            var controlBackup = Path.Combine(StageRoot(state), "retained-" + state.Attempt.ToString("N") + ".control");
            if (File.Exists(work))
            {
                using var completed = new IdentifiedFile(work, flushForPublication: true);
                if (await completed.HashAsync(token) != state.NewHash) throw new IOException("Staged completion changed.");
                if (state.TargetHash is not null && !File.Exists(backup))
                {
                    using var retained = new IdentifiedFile(state.Target);
                    if (await retained.HashAsync(token) != state.TargetHash) throw new IOException("Destination ownership changed.");
                    _fault?.Invoke("completion-checked");
                    retained.RenameWithoutReplacement(backup);
                    _fault?.Invoke("completion-retained-moved");
                }
                if (File.Exists(backup) && (state.TargetHash is null || await HashAsync(backup, token) != state.TargetHash)) throw new IOException("Retained backup changed.");
                // A newly created destination wins the race; never replace it.
                completed.RenameWithoutReplacement(state.Target);
                completed.FlushToDisk();
            }
            if (!File.Exists(state.Target) || await HashAsync(state.Target, token) != state.NewHash) throw new IOException("Completion publication unavailable.");
            _fault?.Invoke("completion-moved");
            if (File.Exists(state.Target + ".aria2"))
            {
                using var control = new IdentifiedFile(state.Target + ".aria2");
                if (state.ControlHash is null || await control.HashAsync(token) != state.ControlHash) throw new IOException("Control ownership changed.");
                control.RenameWithoutReplacement(controlBackup);
            }
            _fault?.Invoke("control-cleaned");
            var published = state with { Phase = "complete-published" };
            await SaveAsync(published, token); Complete(published);
        }
    }
    private static async Task PublishNewCopyAsync(string source, string target, string hash, CancellationToken token)
    {
        if (File.Exists(target)) { if (await HashAsync(target, token) != hash) throw new IOException("Destination collision preserved."); return; }
        if (await HashAsync(source, token) != hash) throw new IOException("Retained snapshot changed.");
        var temporary = source + ".publish-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32768, FileOptions.WriteThrough))
            { await input.CopyToAsync(output, token); await output.FlushAsync(token); output.Flush(true); }
            File.Move(temporary, target);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Unsafe transfer file.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }
    private async Task SaveAsync(State state, CancellationToken token)
    {
        Validate(state); var path = Path.Combine(_root, state.Gid + ".json"); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { await JsonSerializer.SerializeAsync(stream, state, cancellationToken: token); await stream.FlushAsync(token); stream.Flush(true); }
            File.Move(temporary, path, true); _states[state.Gid] = state;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
