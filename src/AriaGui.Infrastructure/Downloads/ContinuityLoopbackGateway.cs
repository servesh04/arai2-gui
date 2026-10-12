using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AriaGui.Infrastructure.Aria2;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace AriaGui.Infrastructure.Downloads;

public sealed class ContinuityLoopbackGateway : IAsyncDisposable
{
    private sealed record Endpoint(int Version, int Port);
    private sealed record Route(int Version, Guid JobId, Guid AttemptId, string Source, string Capability, bool Retained, bool RequirePublicOrigin = true);
    private readonly string _root;
    private readonly Func<int?> _enginePid;
    private readonly Func<string, bool, IContinuityOriginTransport> _origins;
    private readonly ConcurrentDictionary<Guid, (Route Route, HttpContinuityRelay Relay, IContinuityOriginTransport Origin)> _routes = new();
    private readonly ConcurrentDictionary<Guid, byte> _completedRoutes = new();
    private readonly SemaphoreSlim _registration = new(1);
    private FileStream? _lease;
    private WebApplication? _app;
    private int _port;
    private volatile bool _admitting;
    public ContinuityLoopbackGateway(string privateRuntime, Func<int?> enginePid, bool enableExperimental = false)
        : this(privateRuntime, enginePid, (source, requirePublic) => new PublicHttpOriginTransport(source, requirePublic), enableExperimental) { }
    internal ContinuityLoopbackGateway(string privateRuntime, Func<int?> enginePid, Func<string, IContinuityOriginTransport> origins, bool enableExperimental)
        : this(privateRuntime, enginePid, (source, _) => origins(source), enableExperimental) { }
    private ContinuityLoopbackGateway(string privateRuntime, Func<int?> enginePid, Func<string, bool, IContinuityOriginTransport> origins, bool enableExperimental)
    {
        if (!enableExperimental) throw new InvalidOperationException("Continuity gateway is not enabled.");
        if (!Path.IsPathFullyQualified(privateRuntime)) throw new ArgumentException();
        _root = Path.Combine(Path.GetFullPath(privateRuntime), "continuity"); _enginePid = enginePid; _origins = origins;
        UserRuntimeDirectory.EnsurePrivate(_root);
    }
    public async Task StartAsync(CancellationToken token)
    {
        if (_app is not null) throw new InvalidOperationException();
        _lease = new(Path.Combine(_root, "gateway.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var endpointPath = Path.Combine(_root, "endpoint.json");
        var endpoint = File.Exists(endpointPath) ? await ReadAsync<Endpoint>(endpointPath, token) : null;
        if (endpoint is not null && (endpoint.Version != 1 || endpoint.Port is < 1024 or > 65535)) throw new IOException("Invalid gateway ownership.");
        _port = endpoint?.Port ?? 0;
        foreach (var path in Directory.EnumerateFiles(_root, "route-*.json").Take(4097))
        {
            if (_routes.Count >= 4096) throw new IOException("Gateway route capacity exceeded.");
            AddRoute(await ReadAsync<Route>(path, token) ?? throw new IOException("Invalid route record."));
        }
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Production" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Listen(IPAddress.Loopback, _port, listen => listen.Protocols = HttpProtocols.Http1);
            server.Limits.MaxConcurrentConnections = 16; server.Limits.MaxRequestBodySize = 0;
            server.Limits.MaxRequestHeadersTotalSize = 8192; server.Limits.MaxRequestHeaderCount = 16;
            server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5); server.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(5);
        });
        _app = builder.Build(); _app.Run(ServeAsync);
        try
        {
            await _app.StartAsync(token);
            _port = new Uri(_app.Urls.Single()).Port;
            if (endpoint is null) await WriteNewAsync(endpointPath, new Endpoint(1, _port), token);
            _admitting = true;
        }
        catch { await DisposeAsync(); throw; }
    }
    private void AddRoute(Route route)
    {
        if (route.Version != 1 || route.JobId == Guid.Empty || route.AttemptId == Guid.Empty || route.Capability.Length != 64
            || !route.Capability.All(Uri.IsHexDigit)) throw new IOException("Invalid route identity.");
        var origin = _origins(route.Source, route.RequirePublicOrigin);
        var resource = route.RequirePublicOrigin ? route.Source : new UriBuilder(route.Source) { Fragment = "" }.Uri.AbsoluteUri;
        var relay = new HttpContinuityRelay(resource, new(_root, route.JobId, route.AttemptId), origin, route.Retained, enableExperimental: true);
        if (!_routes.TryAdd(route.AttemptId, (route, relay, origin))) { (origin as IDisposable)?.Dispose(); throw new IOException("Duplicate route identity."); }
    }
    public async Task<Uri> RegisterAsync(Guid jobId, Guid attemptId, string exactSource, bool retained, CancellationToken token, bool requirePublicOrigin = true)
    {
        if (!_admitting || jobId == Guid.Empty || attemptId == Guid.Empty) throw new InvalidOperationException("Gateway not ready.");
        await _registration.WaitAsync(token);
        try
        {
            if (_routes.TryGetValue(attemptId, out var existing))
            {
                if (existing.Route.JobId != jobId || existing.Route.Source != exactSource || existing.Route.Retained != retained || existing.Route.RequirePublicOrigin != requirePublicOrigin) throw new IOException("Route conflict.");
                return Address(existing.Route);
            }
            if (_routes.Count >= 4096) throw new IOException("Gateway route capacity exceeded.");
            var route = new Route(1, jobId, attemptId, exactSource, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), retained, requirePublicOrigin);
            // Validate transport/relay construction before durable admission.
            AddRoute(route);
            try { await WriteNewAsync(Path.Combine(_root, "route-" + attemptId.ToString("N") + ".json"), route, token); }
            catch { if (_routes.TryRemove(attemptId, out var rejected)) (rejected.Origin as IDisposable)?.Dispose(); throw; }
            return Address(route);
        }
        finally { _registration.Release(); }
    }
    public Uri GetAddress(Guid attemptId) => _routes.TryGetValue(attemptId, out var route) ? Address(route.Route) : throw new IOException("Route unavailable.");
    internal void CompleteRoute(Guid attemptId) => _completedRoutes.TryAdd(attemptId, 0);
    internal bool MatchesRoute(Guid job, Guid attempt, string source, bool requirePublicOrigin = true) => _routes.TryGetValue(attempt, out var value)
        && value.Route.JobId == job && value.Route.Source == source && value.Route.RequirePublicOrigin == requirePublicOrigin;
    public Task<HttpRepresentation?> GetRepresentationAsync(Guid jobId, CancellationToken token) => new HttpRepresentationStore(_root, jobId).LoadAsync(token);
    public Task<HttpRepresentation?> GetRepresentationAsync(Guid jobId, Guid attemptId, CancellationToken token) => new HttpRepresentationStore(_root, jobId, attemptId).LoadAsync(token);
    public Task<HttpTransferCompletion?> GetCompletionAsync(Guid jobId, Guid attemptId, CancellationToken token) => new HttpRepresentationStore(_root, jobId, attemptId).LoadCompletionAsync(token);
    private Uri Address(Route route) => new($"http://127.0.0.1:{_port}/v1/transfer/{route.AttemptId:N}/{route.Capability}");
    private async Task ServeAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        var parts = context.Request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (!_admitting || context.Request.Method != "GET" || context.Request.Headers.ContainsKey("Origin") || context.Request.ContentLength is > 0
            || context.Request.QueryString.HasValue || parts is not { Length: 4 } || parts[0] != "v1" || parts[1] != "transfer"
            || !Guid.TryParseExact(parts[2], "N", out var id) || _completedRoutes.ContainsKey(id) || !_routes.TryGetValue(id, out var route)
            || parts[3].Length != 64 || !parts[3].All(Uri.IsHexDigit)
            || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(parts[3]), Convert.FromHexString(route.Route.Capability))
            || !LoopbackPeerProcess.IsExpected(context.Connection.RemotePort, _port, _enginePid() ?? 0))
        { context.Response.StatusCode = 403; return; }
        long offset = 0;
        if (context.Request.Headers.TryGetValue("Range", out var header))
        {
            if (header.Count != 1 || !System.Net.Http.Headers.RangeHeaderValue.TryParse(header[0], out var range)
                || range.Unit != "bytes" || range.Ranges.Count != 1 || range.Ranges.Single().From is not long start || start < 0)
            { context.Response.StatusCode = 412; return; }
            offset = start;
        }
        try
        {
            var accepted = await route.Relay.RelayAsync(offset, (response, _) =>
            {
                context.Response.StatusCode = offset > 0 ? 206 : 200;
                if (response.Length >= 0) context.Response.ContentLength = response.Length;
                if (response.StrongETag.Length > 0) { context.Response.Headers.ETag = response.StrongETag; context.Response.Headers.AcceptRanges = "bytes"; }
                if (offset > 0) context.Response.Headers.ContentRange = $"bytes {offset}-{response.Total - 1}/{response.Total}";
                return Task.CompletedTask;
            }, context.Response.Body, context.RequestAborted);
            if (!accepted) context.Response.StatusCode = 412;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or JsonException)
        { if (!context.Response.HasStarted) context.Response.StatusCode = 412;
            // Kestrel closes a short Content-Length response; preserve already flushed bytes.
        }
    }
    private static async Task<T?> ReadAsync<T>(string path, CancellationToken token)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 65536) throw new IOException("Invalid gateway record.");
        return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path, token));
    }
    private static async Task WriteNewAsync<T>(string path, T value, CancellationToken token)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { await JsonSerializer.SerializeAsync(file, value, cancellationToken: token); await file.FlushAsync(token); file.Flush(true); }
            File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async ValueTask DisposeAsync()
    {
        _admitting = false;
        if (_app is not null)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await _app.StopAsync(deadline.Token); } catch (OperationCanceledException) { }
            await _app.DisposeAsync(); _app = null;
        }
        foreach (var route in _routes.Values) (route.Origin as IDisposable)?.Dispose(); _routes.Clear();
        _lease?.Dispose(); _lease = null;
    }
}
