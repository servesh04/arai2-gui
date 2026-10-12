using System.Net;
using System.Net.Http.Headers;
using AriaGui.Infrastructure.Downloads;

namespace AriaGui.Infrastructure.Tests;

public sealed class HttpContinuityRelayTests : IDisposable
{
    private const string Resource = "https://example.invalid/public.bin";
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Fresh_one_shot_completes_with_durable_hash_but_cannot_reconnect(bool weak, bool chunked)
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var store = new HttpRepresentationStore(_root, Guid.NewGuid());
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        { RequestMessage = new(HttpMethod.Get, Resource), Content = chunked ? new UnknownLengthContent(bytes) : new ByteArrayContent(bytes) };
        if (weak) response.Headers.ETag = new("\"weak\"", true);
        var origin = new Origin(response, null);
        var relay = new HttpContinuityRelay(Resource, store, origin, false, true);
        using var body = new MemoryStream();
        Assert.True(await relay.RelayAsync(0, (_, _) => Task.CompletedTask, body, default));
        Assert.Equal(bytes, body.ToArray());
        Assert.False((await store.LoadAsync(default))!.Resumable);
        var receipt = await store.LoadCompletionAsync(default);
        Assert.Equal(bytes.Length, receipt!.Length);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), receipt.Sha256);
        var reopened = new HttpContinuityRelay(Resource, store, origin, false, true);
        Assert.False(await reopened.RelayAsync(0, (_, _) => Task.CompletedTask, body, default));
        Assert.False(await reopened.RelayAsync(2, (_, _) => Task.CompletedTask, body, default));
        Assert.Equal(1, origin.Calls);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(65537)]
    public async Task Known_length_receipt_is_reloadable_before_completion_can_be_observed(int length)
    {
        var id = Guid.NewGuid(); var store = new HttpRepresentationStore(_root, id);
        var bytes = Enumerable.Range(0, length).Select(value => (byte)value).ToArray();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        { RequestMessage = new(HttpMethod.Get, Resource), Content = new ByteArrayContent(bytes) };
        var boundaryChecked = false;
        async Task VerifyReceipt()
        {
            var receipt = await new HttpRepresentationStore(_root, id).LoadCompletionAsync(default);
            Assert.NotNull(receipt); Assert.Equal(length, receipt.Length);
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), receipt.Sha256);
            boundaryChecked = true;
        }
        using var destination = new CompletionBoundaryStream(length, VerifyReceipt);
        var relay = new HttpContinuityRelay(Resource, store, new Origin(response, null), false, true);
        Assert.True(await relay.RelayAsync(0, async (_, _) => { if (length == 0) await VerifyReceipt(); }, destination, default));
        Assert.True(boundaryChecked); Assert.Equal(bytes, destination.ToArray());
    }
    private sealed class CompletionBoundaryStream(int total, Func<Task> onCompletion) : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
        {
            if (Position + bytes.Length == total) await onCompletion();
            await base.WriteAsync(bytes, token);
        }
    }
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AriaGUI.ContinuityTests", Guid.NewGuid().ToString("N"));
    private sealed class Origin(HttpResponseMessage response, string? expectedIfMatch = "\"original\"") : IContinuityOriginTransport
    {
        public int Calls;
        public Task<HttpResponseMessage> OpenAsync(long offset, string? ifMatch, CancellationToken token)
        { Calls++; Assert.Equal(expectedIfMatch, ifMatch); return Task.FromResult(response); }
    }
    private static HttpResponseMessage Response() => new(HttpStatusCode.PartialContent)
    {
        RequestMessage = new(HttpMethod.Get, Resource),
        Content = new ByteArrayContent(new byte[80]),
    };
    [Theory]
    [InlineData("weak")]
    [InlineData("missing")]
    [InlineData("changed")]
    [InlineData("200")]
    [InlineData("412")]
    [InlineData("416")]
    [InlineData("range-start")]
    [InlineData("range-total")]
    [InlineData("range-missing")]
    [InlineData("length")]
    [InlineData("encoding")]
    [InlineData("resource")]
    public async Task Invalid_actual_response_forwards_no_headers_or_body(string mutation)
    {
        var store = new HttpRepresentationStore(_root, Guid.NewGuid());
        await store.SaveBeforeBodyAsync(new(1, Resource, "\"original\"", 100, "identity"), default);
        var response = Response(); response.Headers.ETag = new("\"original\"");
        response.Content.Headers.ContentRange = new(20, 99, 100);
        switch (mutation)
        {
            case "weak": response.Headers.ETag = new("\"original\"", true); break;
            case "missing": response.Headers.ETag = null; break;
            case "changed": response.Headers.ETag = new("\"changed\""); break;
            case "200": response.StatusCode = HttpStatusCode.OK; break;
            case "412": response.StatusCode = HttpStatusCode.PreconditionFailed; break;
            case "416": response.StatusCode = HttpStatusCode.RequestedRangeNotSatisfiable; break;
            case "range-start": response.Content.Headers.ContentRange = new(21, 99, 100); break;
            case "range-total": response.Content.Headers.ContentRange = new(20, 99, 101); break;
            case "range-missing": response.Content.Headers.ContentRange = null; break;
            case "length": response.Content.Headers.ContentLength = 79; break;
            case "encoding": response.Content.Headers.ContentEncoding.Add("gzip"); break;
            case "resource": response.RequestMessage = new(HttpMethod.Get, "https://example.invalid/changed.bin"); break;
        }
        var relay = new HttpContinuityRelay(Resource, store, new Origin(response), true, enableExperimental: true);
        using var body = new MemoryStream(); var headers = 0;
        Assert.False(await relay.RelayAsync(20, (_, _) => { headers++; return Task.CompletedTask; }, body, default));
        Assert.Equal(0, headers); Assert.Equal(0, body.Length);
        Assert.Equal("\"original\"", (await store.LoadAsync(default))!.StrongETag);
    }
    [Fact]
    public async Task Fresh_redirect_binds_final_resource_and_changed_resume_forwards_nothing()
    {
        var store = new HttpRepresentationStore(_root, Guid.NewGuid());
        var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = new(HttpMethod.Get, Resource + "?final"), Content = new ByteArrayContent(new byte[100]) };
        response.Headers.ETag = new("\"original\"");
        using var body = new MemoryStream();
        Assert.True(await new HttpContinuityRelay(Resource, store, new Origin(response, null), false, true).RelayAsync(0, (_, _) => Task.CompletedTask, body, default));
        Assert.Equal(Resource + "?final", (await store.LoadAsync(default))!.EffectiveResource);
        var changed = Response(); changed.RequestMessage = new(HttpMethod.Get, Resource + "?other");
        changed.Headers.ETag = new("\"original\""); changed.Content.Headers.ContentRange = new(20, 99, 100);
        using var resumed = new MemoryStream(); var headers = 0;
        Assert.False(await new HttpContinuityRelay(Resource, store, new Origin(changed), true, true).RelayAsync(20, (_, _) => { headers++; return Task.CompletedTask; }, resumed, default));
        Assert.Equal(0, headers); Assert.Equal(0, resumed.Length);
    }
    [Fact]
    public async Task Legacy_missing_metadata_never_opens_origin_even_at_offset_zero()
    {
        var store = new HttpRepresentationStore(_root, Guid.NewGuid()); var origin = new Origin(Response());
        var relay = new HttpContinuityRelay(Resource, store, origin, true, enableExperimental: true);
        using var body = new MemoryStream();
        Assert.False(await relay.RelayAsync(0, (_, _) => throw new InvalidOperationException(), body, default));
        Assert.Equal(0, origin.Calls);
    }
    [Fact]
    public void Default_gate_refuses_activation() => Assert.Throws<InvalidOperationException>(() =>
        new HttpContinuityRelay(Resource, new(_root, Guid.NewGuid()), new Origin(Response()), false));
    [Fact]
    public async Task Initial_metadata_is_durable_and_reloadable_before_headers_or_body()
    {
        var id = Guid.NewGuid(); var store = new HttpRepresentationStore(_root, id);
        var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = new(HttpMethod.Get, Resource), Content = new ByteArrayContent(new byte[100]) };
        response.Headers.ETag = new("\"original\"");
        var relay = new HttpContinuityRelay(Resource, store, new Origin(response, null), false, enableExperimental: true);
        using var bytes = new MemoryStream();
        Assert.True(await relay.RelayAsync(0, async (_, token) =>
        {
            Assert.Equal(0, bytes.Length);
            Assert.Equal(new HttpRepresentation(1, Resource, "\"original\"", 100, "identity"), await new HttpRepresentationStore(_root, id).LoadAsync(token));
        }, bytes, default));
        Assert.Equal(100, bytes.Length);
    }
    [Fact]
    public async Task Staging_preserves_pair_and_refuses_a_live_writer()
    {
        Directory.CreateDirectory(_root); var retained = Path.Combine(_root, "retained.bin");
        await File.WriteAllBytesAsync(retained, [1, 2, 3]); await File.WriteAllBytesAsync(retained + ".aria2", [4, 5]);
        using (var writer = new FileStream(retained, FileMode.Open, FileAccess.Write, FileShare.Read))
            await Assert.ThrowsAsync<IOException>(() => RetainedPartialStage.CreateAsync(Path.Combine(_root, "stages"), retained, default));
        var stage = await RetainedPartialStage.CreateAsync(Path.Combine(_root, "stages"), retained, default);
        await File.WriteAllBytesAsync(Path.Combine(stage.Directory, stage.FileName), [9]);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(retained));
        Assert.Equal(new byte[] { 4, 5 }, await File.ReadAllBytesAsync(retained + ".aria2"));
    }
    public void Dispose()
    {
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AriaGUI.ContinuityTests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(_root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
