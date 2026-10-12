using System.Net;
using AriaGui.Infrastructure.Downloads;

namespace AriaGui.Infrastructure.Tests;

public sealed class HttpContinuityLimitsTests : IDisposable
{
    private const string Source = "https://example.invalid/file";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AriaGUI.ContinuityLimits", Guid.NewGuid().ToString("N"));
    private sealed class SlowBody(byte[] bytes, int delay) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { await Task.Delay(delay, token); return await base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], token); }
    }
    private sealed class UnknownBody(byte[] bytes) : MemoryStream(bytes)
    { public override bool CanSeek => false; }
    private sealed class Origin(HttpResponseMessage response) : IContinuityOriginTransport
    { public Task<HttpResponseMessage> OpenAsync(long offset, string? match, CancellationToken token) => Task.FromResult(response); }
    private HttpContinuityRelay Relay(Stream body, ContinuityResourceLimits limits, string resource = Source)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = new(HttpMethod.Get, resource), Content = new StreamContent(body) };
        return new(Source, new(_root, Guid.NewGuid()), new Origin(response), false, true, limits);
    }
    [Fact]
    public async Task Progressing_body_can_outlive_idle_timeout()
    {
        using var destination = new MemoryStream(); var bytes = new byte[] { 1, 2, 3, 4, 5, 6 };
        var relay = Relay(new SlowBody(bytes, 60), new() { IdleTimeout = TimeSpan.FromMilliseconds(180), MaximumDuration = TimeSpan.FromSeconds(3) });
        Assert.True(await relay.RelayAsync(0, (_, _) => Task.CompletedTask, destination, default));
        Assert.Equal(bytes, destination.ToArray());
    }
    [Fact]
    public async Task Stalled_body_is_cancelled_before_any_bytes()
    {
        using var destination = new MemoryStream();
        var relay = Relay(new SlowBody([1], 1000), new() { IdleTimeout = TimeSpan.FromMilliseconds(100) });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => relay.RelayAsync(0, (_, _) => Task.CompletedTask, destination, default));
        Assert.Equal(0, destination.Length);
    }
    [Fact]
    public async Task Overall_duration_remains_bounded_despite_progress()
    {
        using var destination = new MemoryStream();
        var relay = Relay(new SlowBody(new byte[100], 50), new() { IdleTimeout = TimeSpan.FromSeconds(1), MaximumDuration = TimeSpan.FromMilliseconds(180) });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => relay.RelayAsync(0, (_, _) => Task.CompletedTask, destination, default));
        Assert.InRange(destination.Length, 1, 99);
    }
    [Fact]
    public async Task Advertised_body_over_byte_limit_forwards_no_headers_or_bytes()
    {
        using var destination = new MemoryStream(); var headers = 0;
        var relay = Relay(new MemoryStream(new byte[5]), new() { MaximumBytes = 4 });
        Assert.False(await relay.RelayAsync(0, (_, _) => { headers++; return Task.CompletedTask; }, destination, default));
        Assert.Equal(0, headers); Assert.Equal(0, destination.Length);
    }
    [Fact]
    public async Task Unknown_length_body_limit_has_no_completion_receipt()
    {
        using var destination = new MemoryStream();
        var relay = Relay(new UnknownBody([1, 2, 3, 4, 5]), new() { MaximumBytes = 4 });
        await Assert.ThrowsAsync<IOException>(() => relay.RelayAsync(0, (_, _) => Task.CompletedTask, destination, default));
        Assert.Equal(0, destination.Length);
        Assert.Empty(Directory.GetFiles(_root, "*.completion.json", SearchOption.AllDirectories));
    }
    public void Dispose()
    {
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AriaGUI.ContinuityLimits")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(_root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
