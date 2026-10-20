using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using AriaGui.Application.Downloads;
using AriaGui.Infrastructure.Aria2;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace AriaGui.Infrastructure.Downloads;

// The only holder of browser credentials. Routes and headers disappear with this process.
public sealed class BrowserCookieRelay(Func<int?> peerPid) : IAsyncDisposable
{
    private sealed record Route(Uri Source, string? Cookie, string? Referrer, string? UserAgent);
    private readonly ConcurrentDictionary<string, Route> _routes = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _start = new(1, 1);
    private readonly HttpClient _origin = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false, UseProxy = false
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private WebApplication? _app;
    private int _port;

    public async Task<Uri> RegisterAsync(BrowserHandoffRequest request, CancellationToken token)
    {
        await _start.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_app is null)
            {
                var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Production" });
                builder.Logging.ClearProviders();
                builder.WebHost.ConfigureKestrel(server =>
                {
                    server.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http1);
                    server.Limits.MaxConcurrentConnections = 16;
                    server.Limits.MaxRequestBodySize = 0;
                    server.Limits.MaxRequestHeadersTotalSize = 8192;
                });
                var app = builder.Build();
                app.Run(ServeAsync);
                await app.StartAsync(token).ConfigureAwait(false);
                _port = new Uri(app.Urls.Single()).Port;
                _app = app;
            }
            var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            if (!_routes.TryAdd(key, new(request.Url, request.Cookies, request.Referrer?.AbsoluteUri, request.UserAgent)))
                throw new IOException("Relay route allocation failed.");
            return new Uri($"http://127.0.0.1:{_port}/handoff/{key}");
        }
        finally { _start.Release(); }
    }

    public void Revoke(Uri address)
    {
        var key = address.AbsolutePath.Split('/').LastOrDefault();
        if (key is not null) _routes.TryRemove(key, out _);
    }

    private async Task ServeAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        var parts = context.Request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (context.Request.Method != "GET" || parts is not { Length: 2 } || parts[0] != "handoff"
            || parts[1].Length != 64 || !parts[1].All(Uri.IsHexDigit)
            || !_routes.TryGetValue(parts[1], out var route)
            || context.Request.QueryString.HasValue || context.Request.Headers.ContainsKey("Origin")
            || context.Request.ContentLength is > 0
            || !LoopbackPeerProcess.IsExpected(context.Connection.RemotePort, _port, peerPid() ?? 0))
        { context.Response.StatusCode = 403; return; }
        try
        {
            var current = route.Source;
            for (var hop = 0; hop <= 5; hop++)
            {
                using var upstream = new HttpRequestMessage(HttpMethod.Get, current);
                upstream.Headers.AcceptEncoding.ParseAdd("identity");
                if (context.Request.Headers.TryGetValue("Range", out var range)
                    && range.Count == 1 && RangeHeaderValue.TryParse(range[0], out var parsedRange))
                    upstream.Headers.Range = parsedRange;
                if (context.Request.Headers.TryGetValue("If-Range", out var ifRange)
                    && ifRange.Count == 1) upstream.Headers.TryAddWithoutValidation("If-Range", ifRange[0]);
                if (route.Referrer is not null) upstream.Headers.TryAddWithoutValidation("Referer", route.Referrer);
                if (route.UserAgent is not null) upstream.Headers.TryAddWithoutValidation("User-Agent", route.UserAgent);
                // Browser cookies are scoped to the initiating origin, including scheme and port.
                if (route.Cookie is not null && current.Scheme == route.Source.Scheme
                    && current.IdnHost == route.Source.IdnHost && current.Port == route.Source.Port)
                    upstream.Headers.TryAddWithoutValidation("Cookie", route.Cookie);
                using var response = await _origin.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
                    or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    var location = response.Headers.Location;
                    if (hop == 5 || location is null || !Uri.TryCreate(current, location, out var next)
                        || next.Scheme is not ("http" or "https") || next.UserInfo.Length != 0
                        || current.Scheme == "https" && next.Scheme != "https")
                        throw new HttpRequestException("Redirect denied.");
                    current = next;
                    continue;
                }
                context.Response.StatusCode = (int)response.StatusCode;
                if (response.Content.Headers.ContentLength is { } length) context.Response.ContentLength = length;
                foreach (var name in new[] { "Content-Range", "Content-Type", "Last-Modified", "ETag", "Accept-Ranges" })
                    if (response.Headers.TryGetValues(name, out var header) || response.Content.Headers.TryGetValues(name, out header))
                        context.Response.Headers[name] = header.ToArray();
                await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
                return;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        { if (!context.Response.HasStarted) context.Response.StatusCode = 502; }
    }

    public async ValueTask DisposeAsync()
    {
        _routes.Clear();
        if (_app is not null) { await _app.StopAsync(); await _app.DisposeAsync(); }
        _origin.Dispose(); _start.Dispose();
    }
}
