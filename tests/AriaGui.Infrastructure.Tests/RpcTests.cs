using System.Net;
using System.Text;
using System.Text.Json;
using AriaGui.Application.Engine;
using AriaGui.Infrastructure.Aria2;

namespace AriaGui.Infrastructure.Tests;

public sealed class RpcTests
{
    [Theory]
    [InlineData("valid", true)]
    [InlineData("active", false)]
    [InlineData("foreign-uri", false)]
    [InlineData("wrong-file", false)]
    [InlineData("missing-uri", false)]
    public async Task Stopped_ownership_requires_terminal_status_exact_uri_and_file(string mutation, bool accepted)
    {
        const string gid = "0123456789abcdef"; const string source = "http://127.0.0.1:18000/v1/owned/capability";
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AriaGUI.RpcOwnership", "payload.bin"));
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); var root = document.RootElement;
            Assert.Equal("aria2.tellStatus", root.GetProperty("method").GetString()); Assert.Equal("token:owned-secret", root.GetProperty("params")[0].GetString());
            var result = new { gid, status = mutation == "active" ? "active" : "removed", files = new[]
            { new { path = mutation == "wrong-file" ? path + ".other" : path, uris = mutation == "missing-uri" ? Array.Empty<object>()
                : new object[] { new { uri = mutation == "foreign-uri" ? "https://example.invalid/direct" : source } } } } };
            return Json(new { jsonrpc = "2.0", id = root.GetProperty("id").GetString(), result });
        }));
        Assert.Equal(accepted, await new Aria2RpcClient(http, new("http://127.0.0.1:16800/jsonrpc"), "owned-secret", TimeSpan.FromSeconds(1))
            .MatchesStoppedOwnershipAsync(gid, source, path, default));
    }
    [Theory]
    [InlineData("duplicates", true)]
    [InlineData("foreign-uri", false)]
    [InlineData("wrong-file", false)]
    [InlineData("malformed-uri", false)]
    [InlineData("empty", false)]
    [InlineData("too-many", false)]
    public async Task Guarded_ownership_requires_every_uri_and_exact_staged_file(string mutation, bool accepted)
    {
        const string source = "http://127.0.0.1:18000/v1/transfer/owned/capability";
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AriaGUI.RpcOwnership", "payload.bin"));
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); var root = document.RootElement;
            Assert.Equal("token:owned-secret", root.GetProperty("params")[0].GetString());
            object[] uris = mutation == "empty" ? [] : mutation == "malformed-uri" ? [new { uri = 1 }]
                : Enumerable.Range(0, mutation == "too-many" ? 17 : 2).Select(index => (object)new { uri = mutation == "foreign-uri" && index == 1 ? "https://example.invalid/direct" : source }).ToArray();
            object result = root.GetProperty("method").GetString() == "aria2.getUris" ? uris
                : new[] { new { path = mutation == "wrong-file" ? path + ".other" : path } };
            return Json(new { jsonrpc = "2.0", id = root.GetProperty("id").GetString(), result });
        }));
        var rpc = new Aria2RpcClient(http, new("http://127.0.0.1:16800/jsonrpc"), "owned-secret", TimeSpan.FromSeconds(1));
        Assert.Equal(accepted, await rpc.MatchesTransferOwnershipAsync("0123456789abcdef", source, path, default));
    }
    [Fact]
    public async Task Exact_signed_source_is_serialized_without_Uri_normalization()
    {
        const string source = "https://example.invalid/file?sig=%41%7e%2f%2Fb&b=2&a=1";
        const string gid = "aaaaaaaaaaaaaaaa";
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); var root = body.RootElement;
            var id = root.GetProperty("id").GetString();
            if (root.GetProperty("method").GetString() == "aria2.addUri")
            { Assert.Equal(source, root.GetProperty("params")[1][0].GetString()); return Json(new { jsonrpc="2.0", id, result=gid }); }
            return Json(new { jsonrpc="2.0", id, result=new { gid, status="waiting", totalLength="0", completedLength="0", downloadSpeed="0", errorCode="0" } });
        }));
        await new Aria2RpcClient(http, new("http://127.0.0.1:16800/jsonrpc"), "synthetic", TimeSpan.FromSeconds(1))
            .AddAsync(new(gid, new(source), ExactSourceUrl:source), default);
    }
    [Fact]
    public async Task Typed_health_requests_use_unique_ids_and_authentication()
    {
        var ids = new List<string>();
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var root = body.RootElement;
            Assert.NotNull(request.Content.Headers.ContentLength);
            Assert.Equal("token:private-test-token", root.GetProperty("params")[0].GetString());
            var id = root.GetProperty("id").GetString()!;
            ids.Add(id);
            var method = root.GetProperty("method").GetString();
            object result = method == "aria2.getVersion"
                ? new { version = "1.37.0", enabledFeatures = new[] { "HTTPS" } }
                : new { sessionId = "session-123" };
            return Json(new { jsonrpc = "2.0", id, result });
        }));
        var client = new Aria2RpcClient(http, new("http://127.0.0.1:16800/jsonrpc"), "private-test-token", TimeSpan.FromSeconds(1));
        var info = await client.GetInfoAsync();
        Assert.Equal("1.37.0", info.Version);
        Assert.Equal("session-123", info.SessionId);
        Assert.Equal(2, ids.Distinct().Count());
    }

    [Theory]
    [InlineData("mismatched-id")]
    [InlineData("invalid-json")]
    [InlineData("missing-result")]
    public async Task Invalid_wire_responses_are_mapped_without_exposing_payloads(string scenario)
    {
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var id = document.RootElement.GetProperty("id").GetString();
            return scenario switch
            {
                "mismatched-id" => Json(new { jsonrpc = "2.0", id = "wrong", result = "sensitive-payload" }),
                "missing-result" => Json(new { jsonrpc = "2.0", id }),
                _ => new(HttpStatusCode.OK) { Content = new StringContent("invalid private-payload") }
            };
        }));
        var ex = await Assert.ThrowsAsync<EngineException>(() => new Aria2RpcClient(http,
            new("http://127.0.0.1:16800/jsonrpc"), "secret", TimeSpan.FromSeconds(1)).GetInfoAsync());
        Assert.Equal(EngineFailure.InvalidResponse, ex.Failure);
        Assert.DoesNotContain("payload", ex.Message);
        Assert.DoesNotContain("secret", ex.Message);
    }

    [Fact]
    public async Task Rpc_authentication_error_does_not_echo_remote_message()
    {
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var response = Json(new { jsonrpc = "2.0", id = doc.RootElement.GetProperty("id").GetString(),
                error = new { code = 1, message = "Unauthorized token:private-test-token https://private.example?key=secret" } });
            response.StatusCode = HttpStatusCode.BadRequest;
            return response;
        }));
        var ex = await Assert.ThrowsAsync<EngineException>(() => new Aria2RpcClient(http,
            new("http://127.0.0.1:16800/jsonrpc"), "private-test-token", TimeSpan.FromSeconds(1)).GetInfoAsync());
        Assert.Equal(EngineFailure.Authentication, ex.Failure);
        Assert.DoesNotContain("private-test-token", ex.Message);
        Assert.DoesNotContain("private.example", ex.Message);
    }

    [Fact]
    public async Task Request_timeout_and_caller_cancellation_have_different_outcomes()
    {
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(HttpStatusCode.OK);
        }));
        var client = new Aria2RpcClient(http, new("http://127.0.0.1:16800/jsonrpc"), "secret", TimeSpan.FromMilliseconds(30));
        var ex = await Assert.ThrowsAsync<EngineException>(() => client.GetInfoAsync());
        Assert.Equal(EngineFailure.RpcUnavailable, ex.Failure);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetInfoAsync(cancellation.Token));
    }

    [Fact]
    public async Task Remote_endpoint_is_refused_before_transmitting_credentials()
    {
        var transmitted = false;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            transmitted = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));
        await Assert.ThrowsAsync<ArgumentException>(() => new Aria2RpcClient(http,
            new("http://example.com/jsonrpc"), "secret", TimeSpan.FromSeconds(1)).GetInfoAsync());
        Assert.False(transmitted);
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Batch_status_uses_bounded_authenticated_subcalls_and_preserves_missing_id_semantics()
    {
        var sizes = new List<int>();
        var ids = Enumerable.Range(1, 130).Select(value => value.ToString("x16")).ToArray();
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); var root = document.RootElement;
            Assert.Equal("system.multicall", root.GetProperty("method").GetString());
            Assert.Equal(1, root.GetProperty("params").GetArrayLength());
            var calls = root.GetProperty("params")[0].EnumerateArray().ToArray(); sizes.Add(calls.Length);
            var result = calls.Select(call =>
            {
                Assert.Equal("aria2.tellStatus", call.GetProperty("methodName").GetString());
                var parameters = call.GetProperty("params"); Assert.Equal("token:batch-secret", parameters[0].GetString());
                Assert.Equal(6, parameters[2].GetArrayLength()); var gid = parameters[1].GetString();
                return gid == ids[129] ? (object)new { code = 1, message = "GID is not found" }
                    : new[] { new { gid, status = "active", totalLength = "100", completedLength = "25", downloadSpeed = "10", errorCode = "0" } };
            }).ToArray();
            return Json(new { jsonrpc = "2.0", id = root.GetProperty("id").GetString(), result });
        }));
        var rows = await new Aria2RpcClient(http, new("http://127.0.0.1:16800/jsonrpc"), "batch-secret", TimeSpan.FromSeconds(1)).FindManyAsync(ids, default);
        Assert.Equal(new[] { 64, 64, 2 }, sizes); Assert.Equal(129, rows.Count); Assert.DoesNotContain(rows, row => row.EngineId == ids[129]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Batch_authentication_and_identity_failures_are_not_treated_as_absent_transfers(bool authentication)
    {
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            object[] result = authentication ? [new { code = 1, message = "Unauthorized private token" }]
                : [new[] { new { gid = "fedcba9876543210", status = "active", totalLength = "100", completedLength = "1", downloadSpeed = "1" } }];
            return Json(new { jsonrpc = "2.0", id = document.RootElement.GetProperty("id").GetString(), result });
        }));
        var ex = await Assert.ThrowsAsync<EngineException>(() => new Aria2RpcClient(http, new("http://127.0.0.1:16800/jsonrpc"), "batch-secret", TimeSpan.FromSeconds(1))
            .FindManyAsync(["0123456789abcdef"], default));
        Assert.Equal(authentication ? EngineFailure.Authentication : EngineFailure.InvalidResponse, ex.Failure); Assert.DoesNotContain("private", ex.Message);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => action(request, token);
    }
}
