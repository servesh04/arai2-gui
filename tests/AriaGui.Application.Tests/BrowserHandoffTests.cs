using System.Text;
using AriaGui.Application.Downloads;
using AriaGui.Application.Engine;

namespace AriaGui.Application.Tests;

public sealed class BrowserHandoffTests
{
    [Theory]
    [InlineData("{\"version\":2,\"url\":\"https://example.com/a\"}")]
    [InlineData("{\"version\":1,\"url\":\"blob:https://example.com/a\"}")]
    [InlineData("{\"version\":1,\"url\":\"file:///C:/secret\"}")]
    [InlineData("{\"version\":1,\"url\":\"https://example.com/a\",\"cookies\":\"x=1\\r\\nHeader: x\"}")]
    [InlineData("{bad")]
    public void RejectsInvalidFrames(string body)
        => Assert.False(BrowserHandoffProtocol.TryParse(Encoding.UTF8.GetBytes(body), out _));

    [Fact]
    public void RejectsOversizedFramesAndNeverSerializesCookiesInResponses()
    {
        Assert.False(BrowserHandoffProtocol.TryParse(new byte[BrowserHandoffProtocol.MaximumPipeBytes + 1], out _));
        var request = Parse("{\"version\":1,\"url\":\"https://example.com/a\",\"cookies\":\"session=unique-secret\"}");
        Assert.DoesNotContain("unique-secret", request.ToString());
        Assert.DoesNotContain("unique-secret", Encoding.UTF8.GetString(new BrowserHandoffResponse(true, Guid.NewGuid()).ToJson()));
    }

    [Fact]
    public async Task GuardsDuplicatesAndEleventhRequestWithinOneSecond()
    {
        var clock = new ManualClock();
        var engine = new EngineStateStore();
        using var lifecycle = new EngineLifecycleService(new ReadyEngine(), engine, clock);
        await lifecycle.StartAsync();
        var sink = new Sink();
        var service = new BrowserHandoffService(new Preferences(), engine, sink, clock);
        var first = Parse("{\"version\":1,\"url\":\"https://example.com/0.zip\"}");
        Assert.True((await service.HandleAsync(first, default)).Ok);
        Assert.Equal("duplicate", (await service.HandleAsync(first, default)).Error);
        for (var i = 1; i < 9; i++)
            Assert.True((await service.HandleAsync(Parse($"{{\"version\":1,\"url\":\"https://example.com/{i}.zip\"}}"), default)).Ok);
        Assert.Equal("rate-limited", (await service.HandleAsync(Parse("{\"version\":1,\"url\":\"https://example.com/10.zip\"}"), default)).Error);
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.True((await service.HandleAsync(first, default)).Ok);
        Assert.Equal(10, sink.Count);
    }

    [Fact]
    public async Task StoppedEngineFailsOpenBeforeSubmitting()
    {
        var sink = new Sink();
        var service = new BrowserHandoffService(new Preferences(), new EngineStateStore(), sink, TimeProvider.System);
        var result = await service.HandleAsync(Parse("{\"version\":1,\"url\":\"https://example.com/a.zip\"}"), default);
        Assert.False(result.Ok);
        Assert.Equal("engine-not-ready", result.Error);
        Assert.Equal(0, sink.Count);
    }

    private static BrowserHandoffRequest Parse(string json)
    {
        Assert.True(BrowserHandoffProtocol.TryParse(Encoding.UTF8.GetBytes(json), out var request));
        return request!;
    }
    private sealed class Preferences : IUserPreferencesRepository
    {
        public Task<UserPreferences> LoadAsync(CancellationToken token) => Task.FromResult(new UserPreferences(
            Browser: new BrowserIntegrationPreferences(Enabled: true, AutomaticTakeoverEnabled: true)));
        public Task SaveAsync(UserPreferences preferences, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class Sink : IBrowserHandoffSubmitter
    {
        public int Count { get; private set; }
        public Task<BrowserHandoffSubmission> SubmitAsync(BrowserHandoffRequest request, bool startPaused, CancellationToken token)
        { Count++; return Task.FromResult(new BrowserHandoffSubmission(Guid.NewGuid(), "file.zip", startPaused)); }
        public Task<bool> UndoAsync(Guid id, CancellationToken token) => Task.FromResult(false);
    }
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
    private sealed class ReadyEngine : IManagedAria2Engine
    {
        public event EventHandler? UnexpectedExit { add { } remove { } }
        public Task<EngineInfo> StartAsync(CancellationToken token) => Task.FromResult(new EngineInfo("1", "test"));
        public Task<EngineInfo> CheckHealthAsync(CancellationToken token) => Task.FromResult(new EngineInfo("1", "test"));
        public Task StopAsync(CancellationToken token) => Task.CompletedTask;
    }
}
