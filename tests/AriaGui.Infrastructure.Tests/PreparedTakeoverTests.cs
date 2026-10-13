using System.Text.Json;
using AriaGui.Application.Downloads;
using AriaGui.Browser.Contracts;
using AriaGui.Core.Downloads;
using AriaGui.Infrastructure.Downloads;
using AriaGui.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AriaGui.Infrastructure.Tests;

public sealed class PreparedTakeoverTests
{
    [Theory]
    [InlineData("policy")]
    [InlineData("consent")]
    [InlineData("grant")]
    [InlineData("private")]
    [InlineData("extension")]
    [InlineData("complete")]
    [InlineData("profile")]
    [InlineData("literal")]
    public async Task Default_off_or_ineligible_item_never_reserves_or_dispatches(string refusal)
    {
        using var fixture = new Fixture(); var request = fixture.Request();
        if (refusal == "consent") await fixture.Preferences.SaveAsync(new(DefaultFolder: fixture.Output), default);
        if (refusal == "grant") request = request with { Download = request.Download with { SourceUrl = "https://other.invalid/file" } };
        if (refusal == "private") request = request with { BrowserItem = request.BrowserItem with { PrivateContext = true } };
        if (refusal == "extension") request = request with { BrowserItem = request.BrowserItem with { InitiatingExtension = "another-extension" } };
        if (refusal == "complete") request = request with { BrowserItem = request.BrowserItem with { Phase = BrowserItemPhase.Complete } };
        if (refusal == "profile") request = request with { BrowserItem = request.BrowserItem with { ProfileKey = "personal-profile-path" } };
        if (refusal == "literal") request = request with { Download = request.Download with { SourceUrl = "http://127.0.0.1/file" } };
        var services = fixture.Services(enabled: refusal != "policy");
        Assert.Equal(PreparedTakeoverPhase.Rejected, (await services.Takeover.PrepareAsync(request)).Phase);
        await services.Coordinator.InitializeAsync(); using var db = fixture.Db(); Assert.Empty(db.ExternalRequests); Assert.Empty(db.PreparedDestinations); Assert.Empty(db.Jobs);
        Assert.Equal(0, fixture.Engine.AddCalls);
        Assert.False(new UserPreferences().EffectiveBrowser.AutomaticTakeoverEnabled);
        Assert.False(JsonSerializer.Deserialize<UserPreferences>("{\"Browser\":{\"Enabled\":true}}")!.EffectiveBrowser.AutomaticTakeoverEnabled);
    }
    [Fact]
    public async Task Prepared_duplicate_survives_restart_without_any_pipeline_rows_and_reserves_destination()
    {
        using var fixture = new Fixture(); var request = fixture.Request(); var services = fixture.Services();
        var prepared = await services.Takeover.PrepareAsync(request); Assert.Equal(PreparedTakeoverPhase.Prepared, prepared.Phase);
        Assert.Equal(fixture.Clock.Now.AddSeconds(60), prepared.ExpiresAt);
        var reopened = fixture.Services(); var duplicate = await reopened.Takeover.PrepareAsync(request); Assert.True(duplicate.Duplicate);
        Assert.Equal(prepared.ExpiresAt, duplicate.ExpiresAt);
        Assert.Equal(PreparedTakeoverPhase.Prepared, (await reopened.Takeover.GetStatusAsync(request.Download.Key)).Phase);
        await Assert.ThrowsAsync<DownloadRequestException>(() => reopened.Coordinator.AddAsync(request.Download.SourceUrl, directory: fixture.Output, fileName: "fixture.bin"));
        Assert.Equal(RejectionReason.DestinationConflict, (await reopened.Intake.AcceptAsync(request.Download with { Key = request.Download.Key with { RequestId = Guid.NewGuid() } })).Reason);
        using var db = fixture.Db(); Assert.Single(db.PreparedDestinations); Assert.Empty(db.Jobs); Assert.Empty(db.Attempts); Assert.Empty(db.Files); Assert.Empty(db.Operations);
        Assert.Equal(0, fixture.Engine.AddCalls);
    }
    [Fact]
    public async Task Concurrent_commit_calls_converge_and_accepted_work_survives_abort_or_opt_out()
    {
        using var fixture = new Fixture(); var request = fixture.Request(); var first = fixture.Services(); var second = fixture.Services();
        await first.Takeover.PrepareAsync(request); var cancelled = fixture.Cancelled(request);
        var receipts = await Task.WhenAll(Enumerable.Range(0, 12).Select(index => (index % 2 == 0 ? first : second).Takeover.CommitAsync(cancelled)));
        Assert.All(receipts, value => Assert.Equal(PreparedTakeoverPhase.Committed, value.Phase));
        Assert.Single(receipts.Select(value => value.Accepted!.JobId).Distinct());
        await first.Intake.DrainAsync(); await second.Intake.DrainAsync();
        await fixture.Preferences.SaveAsync(new(DefaultFolder: fixture.Output), default);
        Assert.Equal(PreparedTakeoverPhase.Committed, (await second.Takeover.AbortAsync(request.Download.Key)).Phase);
        Assert.Equal(PreparedTakeoverPhase.Committed, (await fixture.Services(enabled: false).Takeover.CommitAsync(cancelled)).Phase);
        using var db = fixture.Db(); Assert.Single(db.Jobs); Assert.Single(db.Attempts); Assert.Single(db.Files); Assert.Single(db.Operations); Assert.Empty(db.PreparedDestinations);
        Assert.Null(db.ExternalRequests.Single().PreparedIntentJson); Assert.Equal(1, fixture.Engine.AddCalls);
    }
    [Theory]
    [InlineData("url")]
    [InlineData("filename")]
    [InlineData("item")]
    [InlineData("profile")]
    [InlineData("complete")]
    public async Task Changed_identity_or_unconfirmed_cancel_cannot_commit(string mutation)
    {
        using var fixture = new Fixture(); var request = fixture.Request(); var services = fixture.Services(); await services.Takeover.PrepareAsync(request);
        var changed = fixture.Cancelled(request);
        if (mutation == "url") changed = changed with { Download = changed.Download with { SourceUrl = changed.Download.SourceUrl.Replace("%2F", "%2f", StringComparison.Ordinal) } };
        if (mutation == "filename") changed = changed with { Download = changed.Download with { SuggestedFileName = "changed.bin" } };
        if (mutation == "item") changed = changed with { BrowserItem = changed.BrowserItem with { ItemId = 99 } };
        if (mutation == "profile") changed = changed with { BrowserItem = changed.BrowserItem with { ProfileKey = new string('b', 64) } };
        if (mutation == "complete") changed = changed with { BrowserItem = changed.BrowserItem with { Phase = BrowserItemPhase.Complete } };
        Assert.Equal(PreparedTakeoverPhase.Rejected, (await services.Takeover.CommitAsync(changed)).Phase);
        Assert.Equal(PreparedTakeoverPhase.Aborted, (await services.Takeover.AbortAsync(request.Download.Key)).Phase);
        Assert.Equal(PreparedTakeoverPhase.Aborted, (await services.Takeover.CommitAsync(fixture.Cancelled(request))).Phase);
        using var db = fixture.Db(); Assert.Empty(db.Jobs); Assert.Empty(db.PreparedDestinations); Assert.Null(db.ExternalRequests.Single().PreparedIntentJson);
        Assert.Equal(0, fixture.Engine.AddCalls);
    }
    [Fact]
    public async Task Exact_expiry_releases_reservation_and_cannot_be_extended_by_duplicate_or_commit()
    {
        using var fixture = new Fixture(); var request = fixture.Request(); var services = fixture.Services(); await services.Takeover.PrepareAsync(request);
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(60);
        Assert.Equal(PreparedTakeoverPhase.Expired, (await services.Takeover.CommitAsync(fixture.Cancelled(request))).Phase);
        Assert.Equal(PreparedTakeoverPhase.Expired, (await services.Takeover.PrepareAsync(request)).Phase);
        await services.Coordinator.AddAsync(request.Download.SourceUrl, directory: fixture.Output, fileName: "fixture.bin");
        using var db = fixture.Db(); Assert.Single(db.Jobs); Assert.Empty(db.PreparedDestinations); Assert.Null(db.ExternalRequests.Single().PreparedIntentJson);
        Assert.Equal(1, fixture.Engine.AddCalls);
    }
    [Theory]
    [InlineData("prepare-before-commit", PreparedTakeoverPhase.NotFound)]
    [InlineData("prepare-after-commit", PreparedTakeoverPhase.Prepared)]
    [InlineData("commit-before-commit", PreparedTakeoverPhase.Prepared)]
    [InlineData("commit-after-commit", PreparedTakeoverPhase.Committed)]
    [InlineData("abort-before-commit", PreparedTakeoverPhase.Prepared)]
    [InlineData("abort-after-commit", PreparedTakeoverPhase.Aborted)]
    public async Task Durable_crash_boundaries_reopen_truthfully_and_never_duplicate(string boundary, PreparedTakeoverPhase recovered)
    {
        using var fixture = new Fixture(); var request = fixture.Request();
        if (!boundary.StartsWith("prepare", StringComparison.Ordinal)) await fixture.Services().Takeover.PrepareAsync(request);
        var crashing = fixture.Services(hooks: new(stage => { if (stage == boundary) throw new DownloadStorageException("Synthetic boundary failure."); }));
        if (boundary.StartsWith("prepare", StringComparison.Ordinal)) Assert.Equal(PreparedTakeoverPhase.Unknown, (await crashing.Takeover.PrepareAsync(request)).Phase);
        else if (boundary.StartsWith("commit", StringComparison.Ordinal)) Assert.Equal(PreparedTakeoverPhase.Unknown, (await crashing.Takeover.CommitAsync(fixture.Cancelled(request))).Phase);
        else Assert.Equal(PreparedTakeoverPhase.Unavailable, (await crashing.Takeover.AbortAsync(request.Download.Key)).Phase);
        Assert.Equal(0, fixture.Engine.AddCalls);
        var reopened = fixture.Services(); Assert.Equal(recovered, (await reopened.Takeover.GetStatusAsync(request.Download.Key)).Phase);
        await reopened.Intake.DrainAsync();
        using var db = fixture.Db(); Assert.Equal(recovered == PreparedTakeoverPhase.Committed ? 1 : 0, db.Jobs.Count());
        Assert.Equal(recovered == PreparedTakeoverPhase.Committed ? 1 : 0, fixture.Engine.AddCalls);
    }
    [Fact]
    public async Task Commit_abort_race_has_one_durable_winner_and_never_removes_accepted_work()
    {
        using var fixture = new Fixture(); var request = fixture.Request(); var first = fixture.Services(); var second = fixture.Services(); await first.Takeover.PrepareAsync(request);
        await Task.WhenAll(first.Takeover.CommitAsync(fixture.Cancelled(request)), second.Takeover.AbortAsync(request.Download.Key));
        var status = await first.Takeover.GetStatusAsync(request.Download.Key); await first.Intake.DrainAsync(); await second.Intake.DrainAsync();
        Assert.Contains(status.Phase, new[] { PreparedTakeoverPhase.Committed, PreparedTakeoverPhase.Aborted });
        using var db = fixture.Db(); Assert.Empty(db.PreparedDestinations);
        Assert.Equal(status.Phase == PreparedTakeoverPhase.Committed ? 1 : 0, db.Jobs.Count());
        Assert.Equal(status.Phase == PreparedTakeoverPhase.Committed ? 1 : 0, fixture.Engine.AddCalls);
    }
    [Fact]
    public async Task Existing_schema_upgrades_with_backup_and_SQL_triggers_protect_reserved_destination()
    {
        using var fixture = new Fixture(); using (var old = fixture.Db()) old.GetService<IMigrator>().Migrate("20261011123234_ExternalIntake");
        var services = fixture.Services(); var request = fixture.Request(); await services.Takeover.PrepareAsync(request);
        using var db = fixture.Db(); Assert.Equal(5, db.Database.GetAppliedMigrations().Count()); Assert.False(db.Database.HasPendingModelChanges());
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.Root, "Backups"), "*.db"));
        var key = Path.Combine(fixture.Output, "fixture.bin").ToUpperInvariant();
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Jobs (Id,DisplayName,Source,Destination,DestinationKey,CreatedAtUtcTicks) VALUES ({Guid.NewGuid()},{"synthetic"},{"https://example.invalid/file"},{key},{key},{fixture.Clock.Now.UtcTicks})"));
        Assert.Empty(db.Jobs); Assert.Single(db.PreparedDestinations);
    }
    [Fact]
    public async Task Concurrent_prepares_converge_and_other_key_cannot_steal_reserved_destination()
    {
        using var fixture = new Fixture(); var request = fixture.Request(); var first = fixture.Services(); var second = fixture.Services();
        var responses = await Task.WhenAll(first.Takeover.PrepareAsync(request), second.Takeover.PrepareAsync(request));
        Assert.All(responses, value => Assert.Equal(PreparedTakeoverPhase.Prepared, value.Phase));
        Assert.Equal(responses[0].ExpiresAt, responses[1].ExpiresAt);
        Assert.Equal(PreparedTakeoverPhase.Rejected, (await second.Takeover.PrepareAsync(fixture.Request())).Phase);
        using var db = fixture.Db(); Assert.Single(db.ExternalRequests); Assert.Single(db.PreparedDestinations); Assert.Empty(db.Jobs);
        Assert.Equal(0, fixture.Engine.AddCalls);
    }
    [Fact]
    public async Task Client_cancellation_before_commit_rolls_back_but_after_commit_cannot_revoke_acceptance()
    {
        using var fixture = new Fixture(); var request = fixture.Request(); await fixture.Services().Takeover.PrepareAsync(request);
        using var cancellation = new CancellationTokenSource();
        var before = fixture.Services(hooks: new(stage => { if (stage == "commit-before-commit") cancellation.Cancel(); }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => before.Takeover.CommitAsync(fixture.Cancelled(request), cancellation.Token));
        Assert.Equal(PreparedTakeoverPhase.Prepared, (await fixture.Services().Takeover.GetStatusAsync(request.Download.Key)).Phase);
        using var lostAck = new CancellationTokenSource();
        var after = fixture.Services(hooks: new(stage => { if (stage == "commit-after-commit") lostAck.Cancel(); }));
        Assert.Equal(PreparedTakeoverPhase.Committed, (await after.Takeover.CommitAsync(fixture.Cancelled(request), lostAck.Token)).Phase);
        await after.Intake.DrainAsync(); Assert.Equal(1, fixture.Engine.AddCalls); Assert.Equal(Fixture.Source, fixture.Engine.LastSource);
        using var db = fixture.Db(); Assert.Single(db.Jobs); Assert.Empty(db.PreparedDestinations);
        Assert.Equal(Fixture.Source, db.Jobs.Single().Source);
    }
    private sealed class Clock : TimeProvider { internal DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Engine : IAria2Client
    {
        internal int AddCalls; internal string? LastSource; internal readonly Dictionary<string, EngineDownload> Rows = [];
        public Task<EngineDownload> AddAsync(EngineDownloadRequest request, CancellationToken token)
        { Interlocked.Increment(ref AddCalls); LastSource = request.ExactSourceUrl ?? request.Source.AbsoluteUri; lock (Rows) { Rows[request.EngineId] = new(request.EngineId, new(DownloadStatus.Queued, 0, null, 0)); return Task.FromResult(Rows[request.EngineId]); } }
        public Task<IReadOnlyList<EngineDownload>> GetDownloadsAsync(IReadOnlyList<string> ids, CancellationToken token)
        { lock (Rows) return Task.FromResult<IReadOnlyList<EngineDownload>>(Rows.Values.Where(row => ids.Contains(row.EngineId)).ToArray()); }
    }
    private sealed class Ready : IExternalDispatchReadiness { public Task WaitForReadyAsync(CancellationToken token) => Task.CompletedTask; }
    private sealed record Services(PreparedTakeoverService Takeover, ExternalDownloadIntakeService Intake, DownloadCoordinator Coordinator);
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "AriaGUI.TakeoverTests", Guid.NewGuid().ToString("N"));
        internal string Output => Path.Combine(Root, "output"); internal readonly Clock Clock = new(); internal readonly Engine Engine = new();
        internal JsonUserPreferencesRepository Preferences => new(Path.Combine(Root, "preferences.json"));
        internal const string Source = "https://example.invalid/file?sig=a%2Fb&b=2&a=1";
        internal Fixture() { Directory.CreateDirectory(Root); Preferences.SaveAsync(new(DefaultFolder: Output, Browser: new(Enabled: true, AutomaticTakeoverEnabled: true)), default).GetAwaiter().GetResult(); }
        internal PrepareTakeoverRequest Request() => new(new(new("synthetic-takeover", Guid.Parse("0cb76a97-97a0-46b4-b9d6-6e09962a8e55"), Guid.NewGuid()), Clock.Now, Source, "fixture.bin"), new(7, Clock.Now, new string('a', 64), BrowserItemPhase.InProgress));
        internal PrepareTakeoverRequest Cancelled(PrepareTakeoverRequest value) => value with { BrowserItem = value.BrowserItem with { Phase = BrowserItemPhase.Cancelled } };
        internal Services Services(bool enabled = true, PreparedTakeoverPersistenceHooks? hooks = null)
        {
            var repository = new SqliteDownloadRepository(Path.Combine(Root, "downloads.db"), takeoverClock: Clock, takeoverHooks: hooks);
            var coordinator = new DownloadCoordinator(Engine, new(), Clock, repository, new FileDownloadDestination());
            var folders = new DownloadPreferencesService(Preferences, Output);
            var intake = new ExternalDownloadIntakeService(coordinator, folders, Clock, default, new Ready());
            return new(new(coordinator, intake, Preferences, folders, Clock, new(enabled, [new(Source)]), default), intake, coordinator);
        }
        internal DownloadDbContext Db() => new(new DbContextOptionsBuilder<DownloadDbContext>().UseSqlite($"Data Source={Path.Combine(Root, "downloads.db")};Pooling=False").Options);
        public void Dispose()
        {
            var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AriaGUI.TakeoverTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe synthetic cleanup");
            Directory.Delete(Root, true);
        }
    }
}
