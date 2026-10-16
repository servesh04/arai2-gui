using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.Security;

namespace AriaGui.Infrastructure.Downloads;

// DNS approval and socket connection share the selected address; TLS remains verified
// by SocketsHttpHandler against the original URI hostname. No proxy or redirect bypass.
public sealed class PublicHttpOriginTransport : IContinuityOriginTransport, IDisposable
{
    private readonly Uri _source;
    private readonly HttpClient _http;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly Func<IPEndPoint, CancellationToken, ValueTask<Stream>> _connect;
    private readonly bool _requirePublic;
    public PublicHttpOriginTransport(string exactSource, bool requirePublic = true) : this(exactSource, Dns.GetHostAddressesAsync, ConnectSelectedAsync, null, requirePublic) { }
    internal PublicHttpOriginTransport(string exactSource, Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        Func<IPEndPoint, CancellationToken, ValueTask<Stream>> connect, SslClientAuthenticationOptions? testTls, bool requirePublic = true)
    {
        if (exactSource.Length > 16384 || !Uri.TryCreate(exactSource, UriKind.Absolute, out var source) || source.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(source.UserInfo) || requirePublic && !string.IsNullOrEmpty(source.Fragment)) throw new ArgumentException();
        _source = source.Fragment.Length == 0 ? source : new UriBuilder(source) { Fragment = "" }.Uri;
        _resolve = resolve; _connect = connect; _requirePublic = requirePublic;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None, UseProxy = false, UseCookies = false,
            PooledConnectionLifetime = TimeSpan.Zero, ConnectTimeout = TimeSpan.FromSeconds(10),
            MaxResponseHeadersLength = 16,
            ConnectCallback = ConnectPublicAsync,
        };
        if (testTls is not null) handler.SslOptions = testTls; // Internal synthetic trust only; public constructor never overrides TLS.
        _http = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var ipv4 = address.AddressFamily == AddressFamily.InterNetwork;
        // Conservative policy: special-use ranges are denied even where IANA marks
        // individual anycast/service exceptions globally reachable. IPv6 only2000::/3.
        if (!ipv4 && !InPrefix(address, "2000::", 3)) return false;
        var denied = ipv4 ? V4Denied : V6Denied;
        return !denied.Any(prefix => InPrefix(address, prefix.Address, prefix.Bits));
    }
    private static readonly (string Address, int Bits)[] V4Denied =
    [ ("0.0.0.0",8), ("10.0.0.0",8), ("100.64.0.0",10), ("127.0.0.0",8), ("169.254.0.0",16),
      ("172.16.0.0",12), ("192.0.0.0",24), ("192.0.2.0",24), ("192.88.99.0",24), ("192.168.0.0",16),
      ("198.18.0.0",15), ("198.51.100.0",24), ("203.0.113.0",24), ("224.0.0.0",4), ("240.0.0.0",4) ];
    private static readonly (string Address, int Bits)[] V6Denied =
    [ ("2001::",23), ("2001:db8::",32), ("2002::",16), ("3fff::",20) ];
    private static bool InPrefix(IPAddress address, string network, int bits)
    {
        var actual = address.GetAddressBytes(); var prefix = IPAddress.Parse(network).GetAddressBytes();
        if (actual.Length != prefix.Length) return false;
        for (var index = 0; bits > 0; index++, bits -= 8)
        { var mask = (byte)(0xff << (8 - Math.Min(bits, 8))); if ((actual[index] & mask) != (prefix[index] & mask)) return false; }
        return true;
    }
    private async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var addresses = IPAddress.TryParse(context.DnsEndPoint.Host.Trim('[', ']'), out var literal) ? [literal]
            : await _resolve(context.DnsEndPoint.Host, token);
        if (addresses.Length is 0 or > 64 || addresses.Any(address => !(_requirePublic ? IsPublicAddress(address) : IsManualAddress(address)))) throw new HttpRequestException("Origin address policy denied.");
        foreach (var address in addresses)
        {
            try { return await _connect(new IPEndPoint(address, context.DnsEndPoint.Port), token); }
            catch (SocketException) { }
        }
        throw new HttpRequestException("Origin connection unavailable.");
    }
    private static async ValueTask<Stream> ConnectSelectedAsync(IPEndPoint endpoint, CancellationToken token)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try { await socket.ConnectAsync(endpoint, token); return new NetworkStream(socket, ownsSocket: true); }
        catch { socket.Dispose(); throw; }
    }
    public Task<HttpResponseMessage> OpenAsync(long offset, string? ifMatch, CancellationToken token) => OpenCoreAsync(offset, null, ifMatch, token);
    public Task<HttpResponseMessage> OpenRangeAsync(long start, long end, string? ifMatch, CancellationToken token)
    {
        if (start < 0 || end < start) throw new ArgumentOutOfRangeException(nameof(start));
        return OpenCoreAsync(start, end, ifMatch, token);
    }
    private async Task<HttpResponseMessage> OpenCoreAsync(long offset, long? end, string? ifMatch, CancellationToken token)
    {
        var source = _source; var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var hop = 0; ; hop++)
        {
            if (!seen.Add(source.AbsoluteUri)) throw new HttpRequestException("Origin redirect loop denied.");
            var request = new HttpRequestMessage(HttpMethod.Get, source);
            request.Headers.AcceptEncoding.ParseAdd("identity");
            if (offset > 0 || end is not null) request.Headers.Range = new RangeHeaderValue(offset, end);
            if (ifMatch is not null) request.Headers.IfMatch.ParseAdd(ifMatch);
            var lease = await OriginConnectionBudget.AcquireAsync(source.Host, source.Port, token).ConfigureAwait(false);
            HttpResponseMessage? response = null;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                response.Content = new BudgetedResponseContent(response.Content, lease);
            }
            catch { response?.Dispose(); lease.Dispose(); request.Dispose(); throw; }
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                return response;
            var location = response.Headers.Location;
            response.Dispose(); request.Dispose();
            if (hop >= 5 || location is null || !Uri.TryCreate(source, location, out var next)
                || next.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(next.UserInfo) || !string.IsNullOrEmpty(next.Fragment)
                || next.AbsoluteUri.Length > 16384 || source.Scheme == "https" && next.Scheme != "https")
                throw new HttpRequestException("Origin redirect policy denied.");
            // Each hop resolves and pins its own approved address. No cookies, credentials or proxy.
            source = next;
        }
    }
    internal static bool IsManualAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork ? bytes[0] is > 0 and < 224 && !address.Equals(IPAddress.Broadcast)
            : address.AddressFamily == AddressFamily.InterNetworkV6 && !address.Equals(IPAddress.IPv6Any) && !address.IsIPv6Multicast;
    }
    public void Dispose() => _http.Dispose();
}
