using System.Net;
using AriaGui.Infrastructure.Downloads;

namespace AriaGui.Infrastructure.Tests;

public sealed class ContinuityLoopbackGatewayTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AriaGUI.GatewayTests", Guid.NewGuid().ToString("N"));
    private sealed class Origin(string resource) : IContinuityOriginTransport
    {
        public int Calls;
        public Task<HttpResponseMessage> OpenAsync(long offset, string? ifMatch, CancellationToken token)
        {
            Calls++;
            var response = new HttpResponseMessage(offset > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            { RequestMessage = new(HttpMethod.Get, resource), Content = new ByteArrayContent(new byte[100 - offset]) };
            response.Headers.ETag = new("\"stable\"");
            if (offset > 0) response.Content.Headers.ContentRange = new(offset, 99, 100);
            return Task.FromResult(response);
        }
    }
    private sealed class BoundedOrigin(string resource, byte[] bytes) : IContinuityOriginTransport
    {
        public int Calls;
        public Task<HttpResponseMessage> OpenAsync(long offset, string? ifMatch, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            { RequestMessage = new(HttpMethod.Get, resource), Content = new ByteArrayContent(bytes) };
            response.Headers.ETag = new("\"stable\"");
            return Task.FromResult(response);
        }
        public Task<HttpResponseMessage> OpenRangeAsync(long start, long end, string? ifMatch, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            { RequestMessage = new(HttpMethod.Get, resource), Content = new ByteArrayContent(bytes[(int)start..((int)end + 1)]) };
            response.Headers.ETag = new("\"stable\"");
            response.Content.Headers.ContentRange = new(start, end, bytes.Length);
            return Task.FromResult(response);
        }
    }
    [Fact]
    public async Task Parallel_route_record_rejects_foreign_attempt_filename_on_restart()
    {
        const string source = "https://example.invalid/range.bin";
        var job = Guid.NewGuid(); var attempt = Guid.NewGuid();
        await using (var first = new ContinuityLoopbackGateway(_root, () => Environment.ProcessId,
            _ => new BoundedOrigin(source, new byte[100]), true))
        {
            await first.StartAsync(default);
            await first.RegisterRangeExperimentalAsync(job, attempt, source, default);
        }
        var routePath = Path.Combine(_root, "continuity", "route-" + attempt.ToString("N") + ".json");
        var record = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(routePath))!;
        record["AttemptId"] = Guid.NewGuid().ToString();
        await File.WriteAllTextAsync(routePath, record.ToJsonString());
        await using var restarted = new ContinuityLoopbackGateway(_root, () => Environment.ProcessId,
            _ => new BoundedOrigin(source, new byte[100]), true);
        await Assert.ThrowsAsync<IOException>(() => restarted.StartAsync(default));
    }
    [Fact]
    public async Task Experimental_v2_route_initializes_with_aria2_style_unbounded_get_then_serves_exact_ranges()
    {
        const string source = "https://example.invalid/range.bin";
        var bytes = Enumerable.Range(0, 100).Select(value => (byte)value).ToArray();
        var origin = new BoundedOrigin(source, bytes);
        await using var gateway = new ContinuityLoopbackGateway(_root, () => Environment.ProcessId, _ => origin, true);
        await gateway.StartAsync(default);
        var address = await gateway.RegisterRangeExperimentalAsync(Guid.NewGuid(), Guid.NewGuid(), source, default);
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        using (var unbounded = await client.GetAsync(address))
        {
            Assert.Equal(HttpStatusCode.OK, unbounded.StatusCode);
            Assert.Equal(bytes, await unbounded.Content.ReadAsByteArrayAsync());
        }
        async Task<byte[]> ReadAsync(long start, long end)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.Range = new(start, end);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal($"bytes {start}-{end}/{bytes.Length}", response.Content.Headers.ContentRange?.ToString());
            return await response.Content.ReadAsByteArrayAsync();
        }
        var results = await Task.WhenAll(ReadAsync(0, 49), ReadAsync(50, 99));
        Assert.Equal(bytes, results[0].Concat(results[1]).ToArray());
        Assert.Equal(3, origin.Calls);
    }
    [Theory]
    [InlineData(0, 9)]
    [InlineData(20, 29)]
    public async Task Sequential_route_rejects_bounded_ranges_before_origin_contact(long start, long end)
    {
        const string source = "https://example.invalid/range.bin";
        var origin = new Origin(source);
        await using var gateway = new ContinuityLoopbackGateway(_root, () => Environment.ProcessId, _ => origin, true);
        await gateway.StartAsync(default);
        var address = await gateway.RegisterAsync(Guid.NewGuid(), Guid.NewGuid(), source, false, default);
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.Range = new(start, end);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Equal(0, origin.Calls);
    }
    [Fact]
    public async Task Sequential_resume_accepts_only_a_bounded_tail_ending_at_authenticated_EOF()
    {
        const string source = "https://example.invalid/range.bin";
        var origin = new Origin(source);
        await using var gateway = new ContinuityLoopbackGateway(_root, () => Environment.ProcessId, _ => origin, true);
        await gateway.StartAsync(default);
        var address = await gateway.RegisterAsync(Guid.NewGuid(), Guid.NewGuid(), source, false, default);
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        Assert.Equal(100, (await client.GetByteArrayAsync(address)).Length);
        using var invalid = new HttpRequestMessage(HttpMethod.Get, address);
        invalid.Headers.Range = new(20, 98);
        using var denied = await client.SendAsync(invalid);
        Assert.Equal(HttpStatusCode.PreconditionFailed, denied.StatusCode);
        Assert.Equal(1, origin.Calls);
        using var valid = new HttpRequestMessage(HttpMethod.Get, address);
        valid.Headers.Range = new(20, 99);
        using var accepted = await client.SendAsync(valid);
        Assert.Equal(HttpStatusCode.PartialContent, accepted.StatusCode);
        Assert.Equal("bytes 20-99/100", accepted.Content.Headers.ContentRange?.ToString());
        Assert.Equal(80, (await accepted.Content.ReadAsByteArrayAsync()).Length);
        Assert.Equal(2, origin.Calls);
    }

    [Fact]
    public async Task Manual_lan_route_survives_restart_while_browser_route_never_contacts_private_origin()
    {
        await using var server = new LocalHttpServer(); using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var manual = Guid.NewGuid(); var manualJob = Guid.NewGuid(); Uri address;
        var manualSource = server.Url + "#manual-fragment";
        await using (var gateway = new ContinuityLoopbackGateway(_root, () => Environment.ProcessId, true))
        {
            await gateway.StartAsync(default);
            var browser = await gateway.RegisterAsync(Guid.NewGuid(), Guid.NewGuid(), server.Url, false, default);
            using var denied = await http.GetAsync(browser); Assert.Equal(HttpStatusCode.PreconditionFailed, denied.StatusCode); Assert.Equal(0, server.Requests);
            address = await gateway.RegisterAsync(manualJob, manual, manualSource, false, default, false);
            Assert.Equal(server.Payload, await http.GetByteArrayAsync(address));
            Assert.Equal(server.Url, (await gateway.GetRepresentationAsync(manualJob, default))!.EffectiveResource);
            Assert.True(gateway.MatchesRoute(manualJob, manual, manualSource, false));
            await Assert.ThrowsAsync<IOException>(() => gateway.RegisterAsync(manualJob, manual, manualSource, false, default, true));
        }
        await using (var gateway = new ContinuityLoopbackGateway(_root, () => Environment.ProcessId, true))
        {
            await gateway.StartAsync(default);
            using var request = new HttpRequestMessage(HttpMethod.Get, address); request.Headers.Range = new(32768, null);
            using var response = await http.SendAsync(request); Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal(server.Payload.Skip(32768), await response.Content.ReadAsByteArrayAsync());
        }
    }
    [Fact]
    public async Task Authenticated_route_survives_listener_restart_and_rejects_capability_browser_and_peer()
    {
        const string source = "https://example.invalid/public.bin";
        var job = Guid.NewGuid(); var attempt = Guid.NewGuid(); var origin = new Origin(source);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        Uri address;
        await using (var gateway = new ContinuityLoopbackGateway(_root, () => Environment.ProcessId, _ => origin, true))
        {
            await gateway.StartAsync(default); address = await gateway.RegisterAsync(job, attempt, source, false, default);
            Assert.Equal(100, (await http.GetByteArrayAsync(address)).Length);
            var bad = new Uri(address.AbsoluteUri[..^64] + new string('0', 64));
            Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(bad)).StatusCode);
            using var browser = new HttpRequestMessage(HttpMethod.Get, address); browser.Headers.Add("Origin", "https://example.invalid");
            Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(browser)).StatusCode);
            Assert.Equal(1, origin.Calls);
        }
        await using (var gateway = new ContinuityLoopbackGateway(_root, () => Environment.ProcessId, _ => origin, true))
        {
            await gateway.StartAsync(default); Assert.Equal(address, gateway.GetAddress(attempt));
            using var request = new HttpRequestMessage(HttpMethod.Get, address); request.Headers.Range = new(20, null);
            using var response = await http.SendAsync(request); Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal(80, (await response.Content.ReadAsByteArrayAsync()).Length);
        }
        await using (var gateway = new ContinuityLoopbackGateway(_root, () => Environment.ProcessId + 100000, _ => origin, true))
        {
            await gateway.StartAsync(default);
            Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(address)).StatusCode);
        }
    }
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("169.254.2.3")]
    [InlineData("100.64.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("2001:db8::1")]
    [InlineData("192.0.0.9")]
    [InlineData("192.0.0.255")]
    [InlineData("192.88.99.1")]
    [InlineData("198.19.255.255")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::ffff:192.168.1.1")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("2001:20::1")]
    [InlineData("2001:100::1")]
    [InlineData("2002:7f00:1::1")]
    [InlineData("3fff:fff::1")]
    public void Private_reserved_or_mapped_addresses_are_denied(string address) => Assert.False(PublicHttpOriginTransport.IsPublicAddress(IPAddress.Parse(address)));
    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("2606:4700:4700::1111")]
    public void Public_unicast_addresses_are_eligible_without_connecting(string address) => Assert.True(PublicHttpOriginTransport.IsPublicAddress(IPAddress.Parse(address)));
    [Fact]
    public async Task Actual_loopback_socket_is_never_opened_by_production_transport()
    {
        await using var server = new LocalHttpServer(); using var origin = new PublicHttpOriginTransport(server.Url);
        await Assert.ThrowsAsync<HttpRequestException>(() => origin.OpenAsync(0, null, default));
        Assert.Equal(0, server.Requests);
    }
    public void Dispose()
    {
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AriaGUI.GatewayTests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(_root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
