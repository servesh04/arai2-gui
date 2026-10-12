using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AriaGui.Infrastructure.Downloads;

namespace AriaGui.Infrastructure.Tests;

public sealed class PublicOriginNetworkTests
{
    private sealed class Fixture(X509Certificate2? certificate = null, bool redirect = false, string? location = null) : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(15));
        private Task? _work;
        public int Port { get; private set; }
        public int Requests;
        public string? Host;
        public string? Headers;
        public string? Location = location;
        public Exception? Failure;
        public void Start()
        {
            _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _work = Task.Run(async () =>
            {
                try
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    await using var network = client.GetStream();
                    await using Stream stream = certificate is null ? network : new SslStream(network, true);
                    if (stream is SslStream tls) await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, _stop.Token);
                    var bytes = new byte[8192]; var count = 0;
                    while (count < bytes.Length)
                    {
                        var read = await stream.ReadAsync(bytes.AsMemory(count), _stop.Token); if (read == 0) return; count += read;
                        if (Encoding.ASCII.GetString(bytes, 0, count).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
                    }
                    Interlocked.Increment(ref Requests);
                    Headers = Encoding.ASCII.GetString(bytes, 0, count);
                    Host = Headers.Split("\r\n").Single(line => line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase));
                    var reply = redirect ? $"HTTP/1.1 302 Found\r\nLocation: {Location ?? "http://127.0.0.1/private"}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                        : "HTTP/1.1 200 OK\r\nContent-Length: 4\r\nETag: \"strong\"\r\nConnection: close\r\n\r\nbody";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(reply), _stop.Token); await stream.FlushAsync(_stop.Token);
                }
                catch (Exception ex) when (ex is IOException or AuthenticationException or OperationCanceledException or SocketException) { Failure = ex; }
            });
        }
        public async ValueTask<Stream> Connect(IPAddress approved, IPEndPoint endpoint, CancellationToken token)
        {
            Assert.Equal(approved, endpoint.Address); Assert.Equal(Port, endpoint.Port);
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try { await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, Port), token); return new NetworkStream(client, true); }
            catch { client.Dispose(); throw; }
        }
        public async ValueTask DisposeAsync() { _stop.Cancel(); _listener.Stop(); if (_work is not null) await _work; _stop.Dispose(); }
    }
    [Fact]
    public async Task Public_redirect_checks_each_hop_and_records_final_resource()
    {
        await using var final = new Fixture(); final.Start();
        var resource = $"http://final.fixture.invalid:{final.Port}/file";
        await using var first = new Fixture(redirect: true, location: resource); first.Start();
        var address = IPAddress.Parse("8.8.8.8"); var names = new List<string>();
        using var transport = new PublicHttpOriginTransport($"http://public.fixture.invalid:{first.Port}/start", (name, _) => { names.Add(name); return Task.FromResult(new[] { address }); },
            (endpoint, token) => (endpoint.Port == first.Port ? first : final).Connect(address, endpoint, token), null);
        using var response = await transport.OpenAsync(2, "\"old\"", default);
        Assert.Equal("body", await response.Content.ReadAsStringAsync()); Assert.Equal(resource, response.RequestMessage!.RequestUri!.OriginalString);
        Assert.Equal(new[] { "public.fixture.invalid", "final.fixture.invalid" }, names);
        Assert.Contains("Range: bytes=2-", first.Headers); Assert.Contains("Range: bytes=2-", final.Headers);
        Assert.Contains("If-Match: \"old\"", final.Headers);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Redirect_loop_and_excess_hops_are_bounded(bool loop)
    {
        var fixtures = Enumerable.Range(0, loop ? 1 : 7).Select(_ => new Fixture(redirect: true)).ToArray();
        try
        {
            foreach (var fixture in fixtures) fixture.Start();
            var names = fixtures.Select(fixture => $"http://public.fixture.invalid:{fixture.Port}/file").ToArray();
            for (var index = 0; index < fixtures.Length; index++) fixtures[index].Location = names[loop ? 0 : Math.Min(index + 1, fixtures.Length - 1)];
            var address = IPAddress.Parse("8.8.8.8"); var dials = 0;
            using var transport = new PublicHttpOriginTransport(names[0], (_, _) => Task.FromResult(new[] { address }),
                (endpoint, token) => { dials++; return fixtures.Single(fixture => fixture.Port == endpoint.Port).Connect(address, endpoint, token); }, null);
            await Assert.ThrowsAsync<HttpRequestException>(() => transport.OpenAsync(0, null, default));
            Assert.Equal(loop ? 1 : 6, dials); if (!loop) Assert.Equal(0, fixtures[^1].Requests);
        }
        finally { foreach (var fixture in fixtures) await fixture.DisposeAsync(); }
    }
    [Fact]
    public async Task Exact_signed_path_and_query_survive_actual_request_serialization()
    {
        await using var fixture = new Fixture(); fixture.Start(); var address = IPAddress.Parse("8.8.8.8");
        const string path = "/a%2fb?z=%2f&a=%2F&sig=ordered";
        using var transport = new PublicHttpOriginTransport($"http://public.fixture.invalid:{fixture.Port}" + path, (_, _) => Task.FromResult(new[] { address }),
            (endpoint, token) => fixture.Connect(address, endpoint, token), null);
        using var response = await transport.OpenAsync(0, null, default);
        Assert.StartsWith("GET " + path + " HTTP/1.1\r\n", fixture.Headers);
    }
    [Theory]
    [InlineData("ftp://public.fixture.invalid/file")]
    [InlineData("http://user:secret@public.fixture.invalid/file")]
    [InlineData("http://public.fixture.invalid/file#fragment")]
    public async Task Redirect_rejects_unsupported_destination_without_second_connection(string destination)
    {
        await using var first = new Fixture(redirect: true, location: destination); first.Start(); var address = IPAddress.Parse("8.8.8.8"); var dials = 0;
        using var transport = new PublicHttpOriginTransport($"http://public.fixture.invalid:{first.Port}/start", (_, _) => Task.FromResult(new[] { address }),
            (endpoint, token) => { dials++; return first.Connect(address, endpoint, token); }, null);
        await Assert.ThrowsAsync<HttpRequestException>(() => transport.OpenAsync(0, null, default)); Assert.Equal(1, dials);
    }
    [Fact]
    public async Task Connection_rechecks_dns_and_pins_approved_address_without_second_resolution()
    {
        await using var fixture = new Fixture(); fixture.Start(); var resolved = 0; var dialed = 0; var address = IPAddress.Parse("8.8.8.8");
        using var transport = new PublicHttpOriginTransport($"http://public.fixture.invalid:{fixture.Port}/file", (_, _) =>
            Task.FromResult(Interlocked.Increment(ref resolved) == 1 ? new[] { address } : new[] { IPAddress.Loopback }),
            (endpoint, token) => { Interlocked.Increment(ref dialed); return fixture.Connect(address, endpoint, token); }, null);
        using (var response = await transport.OpenAsync(0, null, default)) Assert.Equal("body", await response.Content.ReadAsStringAsync());
        await Assert.ThrowsAsync<HttpRequestException>(() => transport.OpenAsync(0, null, default));
        Assert.Equal(2, resolved); Assert.Equal(1, dialed); Assert.Equal(1, fixture.Requests);
        Assert.Contains("public.fixture.invalid", fixture.Host);
    }
    [Fact]
    public async Task Mixed_public_private_dns_answer_never_dials()
    {
        var dialed = false;
        using var transport = new PublicHttpOriginTransport("http://public.fixture.invalid/file", (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Loopback }),
            (_, _) => { dialed = true; throw new InvalidOperationException(); }, null);
        await Assert.ThrowsAsync<HttpRequestException>(() => transport.OpenAsync(0, null, default)); Assert.False(dialed);
    }
    [Fact]
    public async Task Redirect_to_private_target_is_not_followed()
    {
        await using var fixture = new Fixture(redirect: true); fixture.Start(); var address = IPAddress.Parse("8.8.8.8"); var calls = 0;
        using var transport = new PublicHttpOriginTransport($"http://public.fixture.invalid:{fixture.Port}/file", (_, _) => { calls++; return Task.FromResult(new[] { address }); },
            (endpoint, token) => fixture.Connect(address, endpoint, token), null);
        await Assert.ThrowsAsync<HttpRequestException>(() => transport.OpenAsync(0, null, default));
        Assert.Equal(1, calls); Assert.Equal(1, fixture.Requests);
    }
    [Theory]
    [InlineData("good")]
    [InlineData("mismatch")]
    [InlineData("downgrade")]
    public async Task Tls_authenticates_original_hostname_on_pinned_socket(string scenario)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=synthetic", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName(scenario == "mismatch" ? "other.fixture.invalid" : "public.fixture.invalid"); request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Windows SChannel needs a temporary provider key. No PersistKeySet and no
        // certificate-store installation; disposal removes the imported key.
        using var serverCertificate = X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.DefaultKeySet);
        var chain = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck }; chain.CustomTrustStore.Add(serverCertificate);
        await using var fixture = new Fixture(serverCertificate, redirect: scenario == "downgrade"); fixture.Start(); var address = IPAddress.Parse("8.8.8.8"); var dials = 0;
        using var transport = new PublicHttpOriginTransport($"https://public.fixture.invalid:{fixture.Port}/file", (_, _) => Task.FromResult(new[] { address }),
            (endpoint, token) => { dials++; return fixture.Connect(address, endpoint, token); }, new SslClientAuthenticationOptions { CertificateChainPolicy = chain });
        if (scenario == "downgrade")
        {
            var failure = await Assert.ThrowsAsync<HttpRequestException>(() => transport.OpenAsync(0, null, default));
            Assert.Contains("redirect policy denied", failure.Message); Assert.Equal(1, dials); Assert.Equal(1, fixture.Requests);
        }
        else if (scenario == "mismatch")
        {
            var failure = await Assert.ThrowsAsync<HttpRequestException>(() => transport.OpenAsync(0, null, default));
            Assert.Contains("RemoteCertificateNameMismatch", failure.ToString()); Assert.Equal(0, fixture.Requests);
        }
        else
        {
            HttpResponseMessage response;
            try { response = await transport.OpenAsync(0, null, default); }
            catch (HttpRequestException) { throw new Xunit.Sdk.XunitException("Synthetic TLS server failure: " + fixture.Failure); }
            using (response) Assert.Equal("body", await response.Content.ReadAsStringAsync()); Assert.Contains("public.fixture.invalid", fixture.Host);
        }
    }
}
