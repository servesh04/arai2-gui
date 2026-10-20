using System.Diagnostics;
using System.Text;
using AriaGui.Application.Downloads;
using AriaGui.Core.Downloads;
using AriaGui.Infrastructure.Downloads;
using AriaGui.Infrastructure.Persistence;

namespace AriaGui.Infrastructure.Tests;

public sealed class BrowserHandoffPersistenceTests
{
    [Fact]
    public async Task CookieNeverReachesHistoryAndUndoOnlyDeletesOwnedPartialFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "AriaGUI.BrowserHandoffTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var downloads = Path.Combine(root, "downloads"); Directory.CreateDirectory(downloads);
            var preferences = new JsonUserPreferencesRepository(Path.Combine(root, "preferences.json"));
            await preferences.SaveAsync(new UserPreferences(DefaultFolder: downloads,
                Browser: new BrowserIntegrationPreferences(Enabled: true, AutomaticTakeoverEnabled: true)), default);
            var repository = new SqliteDownloadRepository(Path.Combine(root, "history.db"));
            var engine = new BrowserEngine(); var store = new DownloadRuntimeStore();
            var coordinator = new DownloadCoordinator(engine, store, TimeProvider.System, repository, new FileDownloadDestination());
            await using var relay = new BrowserCookieRelay(() => Process.GetCurrentProcess().Id);
            using var submitter = new BrowserHandoffSubmitter(coordinator,
                new DownloadPreferencesService(preferences, downloads), relay, store);
            var secret = "handoff-cookie-private-value";
            Assert.True(BrowserHandoffProtocol.TryParse(Encoding.UTF8.GetBytes(
                "{\"version\":1,\"url\":\"http://127.0.0.1:50000/large.zip\",\"filename\":\"large.zip\",\"cookies\":\"sid=" + secret + "\"}"), out var request));
            var result = await submitter.SubmitAsync(request!, false, default);
            var path = Path.Combine(downloads, "large.zip");
            await File.WriteAllTextAsync(path, "partial");
            await File.WriteAllTextAsync(path + ".aria2", "control");
            var attempt = (await coordinator.GetBrowserEntryAsync(result.Id, default))!.Attempt.Id;
            var stage = Path.Combine(downloads, ".aria-gui-continuity-" + result.Id.ToString("N"), attempt.ToString("N"));
            Directory.CreateDirectory(stage);
            await File.WriteAllTextAsync(Path.Combine(stage, "large.zip"), "private partial");
            await File.WriteAllTextAsync(Path.Combine(stage, "large.zip.aria2"), "private control");
            var otherStage = Path.Combine(downloads, ".aria-gui-continuity-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(otherStage);
            await File.WriteAllTextAsync(Path.Combine(otherStage, "keep.aria2"), "unrelated control");
            var unrelated = Path.Combine(downloads, "keep.txt");
            await File.WriteAllTextAsync(unrelated, "keep");
            var outside = Path.Combine(root, "outside.txt");
            await File.WriteAllTextAsync(outside, "keep");
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                Assert.DoesNotContain(secret, Encoding.Latin1.GetString(await File.ReadAllBytesAsync(file)));
            Assert.True(await submitter.UndoAsync(result.Id, default));
            Assert.False(File.Exists(path)); Assert.False(File.Exists(path + ".aria2"));
            Assert.Empty(Directory.EnumerateFiles(stage));
            Assert.True(File.Exists(Path.Combine(otherStage, "keep.aria2")));
            Assert.True(File.Exists(unrelated)); Assert.True(File.Exists(outside));
            Assert.True(BrowserHandoffProtocol.TryParse(Encoding.UTF8.GetBytes(
                "{\"version\":1,\"url\":\"http://127.0.0.1:50000/finished.zip\",\"filename\":\"finished.zip\"}"), out var finished));
            var completed = await submitter.SubmitAsync(finished!, false, default);
            var completePath = Path.Combine(downloads, "finished.zip");
            await File.WriteAllTextAsync(completePath, "complete");
            engine.Complete((await coordinator.GetBrowserEntryAsync(completed.Id, default))!.Attempt.EngineId);
            await coordinator.RefreshAsync();
            Assert.False(await submitter.UndoAsync(completed.Id, default));
            Assert.True(File.Exists(completePath));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class BrowserEngine : IAria2Client
    {
        private readonly Dictionary<string, DownloadStatus> _jobs = [];
        public void Complete(string gid) => _jobs[gid] = DownloadStatus.Completed;
        public Task<EngineDownload> AddAsync(EngineDownloadRequest request, CancellationToken token)
        {
            _jobs.Add(request.EngineId, DownloadStatus.Queued);
            return Task.FromResult(new EngineDownload(request.EngineId, new(DownloadStatus.Queued, 0, 100, 0)));
        }
        public Task<IReadOnlyList<EngineDownload>> GetDownloadsAsync(IReadOnlyList<string> ids, CancellationToken token)
            => Task.FromResult<IReadOnlyList<EngineDownload>>(ids.Where(_jobs.ContainsKey)
                .Select(id => new EngineDownload(id, new(_jobs[id], 0, 100, 0))).ToArray());
        public Task<EngineCommandResult> ExecuteAsync(string gid, DownloadAction action, int? position, CancellationToken token)
        {
            if (action != DownloadAction.Remove) throw new NotSupportedException();
            _jobs[gid] = DownloadStatus.Removed;
            return Task.FromResult(new EngineCommandResult("test", new EngineDownload(gid, new(DownloadStatus.Removed, 0, 100, 0))));
        }
    }
}
