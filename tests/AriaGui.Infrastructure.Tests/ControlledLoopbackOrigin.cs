using System.Net;
using System.Net.Http.Headers;
using AriaGui.Infrastructure.Downloads;

namespace AriaGui.Infrastructure.Tests;

internal sealed class ControlledLoopbackOrigin : IContinuityOriginTransport, IDisposable
{
    private readonly Uri _source;
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None });
    public ControlledLoopbackOrigin(string source)
    {
        _source = new(source);
        if (_source.Scheme != "http" || !IPAddress.TryParse(_source.Host, out var address) || !IPAddress.IsLoopback(address)) throw new ArgumentException();
    }
    public async Task<HttpResponseMessage> OpenAsync(long offset, string? ifMatch, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _source); request.Headers.AcceptEncoding.ParseAdd("identity");
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        if (ifMatch is not null) request.Headers.IfMatch.ParseAdd(ifMatch);
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
    }
    public void Dispose() => _http.Dispose();
}
