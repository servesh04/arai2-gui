using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AriaGui.Infrastructure.Aria2;

namespace AriaGui.Browser.Spikes;

// Opt-in, loopback-only feasibility experiment. Raw synthetic artifacts intentionally remain
// in the chosen fresh directory so persistence findings can be independently reproduced.
internal static class CredentialSpike
{
    internal static async Task<object> ProbeAsync(string binaryPath, string outputPath)
    {
        binaryPath = Path.GetFullPath(binaryPath);
        var root = Path.GetFullPath(outputPath);
        Program.Check(!Directory.Exists(root) || !Directory.EnumerateFileSystemEntries(root).Any(), "Fresh artifact directory required");
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(binaryPath)));
        Program.Check(hash == Aria2ProcessOptions.BundledSha256, "Pinned binary required");
        Directory.CreateDirectory(root);
        var markers = new Dictionary<string, string>
        {
            ["cookie"] = "PHASEB_COOKIE_" + Guid.NewGuid().ToString("N"),
            ["authorization"] = "PHASEB_AUTH_" + Guid.NewGuid().ToString("N"),
            ["customHeader"] = "PHASEB_HEADER_" + Guid.NewGuid().ToString("N"),
            ["userAgent"] = "PHASEB_UA_" + Guid.NewGuid().ToString("N"),
            ["referrer"] = "PHASEB_REF_" + Guid.NewGuid().ToString("N")
        };
        var observations = new ConcurrentQueue<RequestObservation>();
        await using var target = new Origin(markers, observations, "target");
        await using var source = new Origin(markers, observations, "source", target.Url("/file"));
        await using var secure = await TlsProxy.StartAsync(root, new Uri(source.Url("/")).Port);
        var scans = new List<ScanObservation>();
        string[] recoveredFields;
        var lifecycle = Path.Combine(root, "lifecycle"); Directory.CreateDirectory(lifecycle);
        string session;
        await using (var engine = await Engine.StartAsync(binaryPath, lifecycle))
        {
            var version = await engine.RpcAsync("getVersion");
            Program.Check(version.GetProperty("version").GetString() == Aria2ProcessOptions.BundledVersion, "Pinned engine version");
            var gid = await AddAsync(engine, source.Url("/slow"), markers, "lifecycle.bin", paused: true);
            await engine.RpcAsync("saveSession");
            scans.Add(Scan(root, "normal-save", markers));
            // Remove the first snapshot; the next one must be the autonomous five-second save.
            File.Delete(engine.Session);
            await WaitAsync(() => File.Exists(engine.Session), "Periodic session save", TimeSpan.FromSeconds(9));
            scans.Add(Scan(root, "periodic-save", markers));
            await engine.RpcAsync("unpause", gid);
            await WaitAsync(() => observations.Any(), "Active request");
            await engine.RpcAsync("forcePause", gid);
            await engine.RpcAsync("saveSession");
            scans.Add(Scan(root, "suspend-checkpoint", markers));
            await engine.ShutdownAsync();
            scans.Add(Scan(root, "graceful-shutdown", markers));
            session = engine.Session;
        }
        var recovery = Path.Combine(root, "recovery"); Directory.CreateDirectory(recovery);
        File.Copy(session, Path.Combine(recovery, "restore.txt"));
        await using (var engine = await Engine.StartAsync(binaryPath, recovery, Path.Combine(recovery, "restore.txt")))
        {
            var restored = await engine.RpcAsync("tellWaiting", 0, 10);
            Program.Check(restored.GetArrayLength() == 1, "One restored suspended transfer");
            var gid = restored[0].GetProperty("gid").GetString()!;
            var options = await engine.RpcAsync("getOption", gid);
            var serializedOptions = options.GetRawText();
            recoveredFields = markers.Where(m => serializedOptions.Contains(m.Value, StringComparison.Ordinal)).Select(m => m.Key).ToArray();
            await engine.RpcAsync("saveSession");
            scans.Add(Scan(root, "recovery-save", markers));
            await engine.RpcAsync("unpause", gid);
            await Task.Delay(300);
            await engine.RpcAsync("saveSession");
            engine.Kill();
            scans.Add(Scan(root, "forced-crash", markers));
        }
        // Preserve the crash checkpoint and prove it can restore secrets again.
        var crash = Path.Combine(root, "crash-recovery"); Directory.CreateDirectory(crash);
        File.Copy(Path.Combine(recovery, "session.txt"), Path.Combine(crash, "restore.txt"));
        await using (var engine = await Engine.StartAsync(binaryPath, crash, Path.Combine(crash, "restore.txt")))
        {
            var restored = await engine.RpcAsync("tellWaiting", 0, 10);
            Program.Check(restored.GetArrayLength() == 1, "Crash session restores suspended transfer");
            await engine.RpcAsync("saveSession");
            await engine.ShutdownAsync();
            scans.Add(Scan(root, "forced-crash-recovery", markers));
        }
        var redirects = Path.Combine(root, "redirects"); Directory.CreateDirectory(redirects);
        await using (var engine = await Engine.StartAsync(binaryPath, redirects))
        {
            foreach (var (name, url) in new[]
            {
                ("same-origin", source.Url("/same?case=same-origin")),
                ("cross-origin-port", source.Url("/cross?case=cross-origin-port")),
                ("cross-origin-host", source.Url("/host?case=cross-origin-host")),
                ("https-downgrade", secure.Url("/cross?case=https-downgrade"))
            })
            {
                var gid = await AddAsync(engine, url, markers, name + ".bin", paused: false);
                await WaitAsync(() => observations.Any(o => o.Route == "/file?case=" + name), "Redirect target observed");
                var completed = false;
                for (var attempt = 0; attempt < 50 && !completed; attempt++)
                {
                    var result = await engine.RpcAsync("tellStatus", gid);
                    Program.Check(result.GetProperty("status").GetString() != "error", "Redirect transfer success");
                    completed = result.GetProperty("status").GetString() == "complete";
                    if (!completed) await Task.Delay(100);
                }
                Program.Check(completed && (await File.ReadAllBytesAsync(Path.Combine(redirects, name + ".bin"))).AsSpan().SequenceEqual("test"u8), "Completed redirect output matches expected bytes");
            }
            await engine.ShutdownAsync();
            scans.Add(Scan(root, "redirect-shutdown", markers));
        }
        bool AllFields(IEnumerable<string> fields) => markers.Keys.All(fields.Contains);
        var lifecycleChecks = scans.Where(s => s.Boundary != "redirect-shutdown").Select(s => new
        {
            boundary = s.Boundary,
            allFieldsPersisted = s.Files.Any(f => f.Path == Path.Combine(s.Boundary is "normal-save" or "periodic-save" or "suspend-checkpoint" or "graceful-shutdown"
                ? "lifecycle" : s.Boundary is "recovery-save" or "forced-crash" ? "recovery" : "crash-recovery", "session.txt") && AllFields(f.MarkerFields))
        }).ToArray();
        var redirectChecks = new[] { "same-origin", "cross-origin-port", "cross-origin-host", "https-downgrade" }.Select(name => new
        { name, allFieldsForwarded = observations.Any(o => o.Route == "/file?case=" + name && o.Server == (name == "same-origin" ? "source" : "target") && AllFields(o.MarkerFields)) }).ToArray();
        var allPersisted = lifecycleChecks.All(c => c.allFieldsPersisted) && AllFields(recoveredFields)
            && scans.Last().Files.Where(f => f.Path.EndsWith("restore.txt", StringComparison.Ordinal)).Count(f => AllFields(f.MarkerFields)) == 2;
        var allForwarded = redirectChecks.All(c => c.allFieldsForwarded);
        var evidence = new
        {
            engineVersion = Aria2ProcessOptions.BundledVersion, executableSha256 = hash,
            syntheticOnly = true, personalBrowserUsed = false,
            certificateValidationDisabledOnlyForSyntheticTls = true,
            productionFeatureExpanded = false,
            measurementStatus = allPersisted && allForwarded ? "observed_blockers" : "changed_inconclusive",
            persistenceGate = allPersisted ? "blocked" : "inconclusive", redirectGate = allForwarded ? "blocked" : "inconclusive",
            scope = "Raw engine RPC header options; current application contract cannot carry credentials. SQLite/WAL/backups are absent by construction, not certified credential-safe. Session copies emulate retained restore artifacts; no real app recovery/diagnostics claim.",
            lifecycleChecks, redirectChecks, recoveredFields, scans, requests = observations.ToArray()
        };
        await File.WriteAllTextAsync(Path.Combine(root, "evidence.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        Program.Check(allPersisted && allForwarded, "Changed or inconclusive engine behavior; inspect recorded per-case checks");
        return evidence;
    }

    private static async Task<string> AddAsync(Engine engine, string url, Dictionary<string, string> markers, string file, bool paused)
    {
        var options = new Dictionary<string, object>
        {
            ["dir"] = engine.Root, ["out"] = file, ["pause"] = paused ? "true" : "false",
            ["header"] = new[] { "Cookie: synthetic=" + markers["cookie"], "Authorization: Bearer " + markers["authorization"], "X-Synthetic: " + markers["customHeader"] },
            ["user-agent"] = markers["userAgent"], ["referer"] = "https://example.invalid/" + markers["referrer"],
            ["check-certificate"] = "false", ["split"] = "1", ["max-connection-per-server"] = "1"
        };
        return (await engine.RpcAsync("addUri", new[] { url }, options)).GetString()!;
    }

    private sealed record FileObservation(string Path, int Bytes, string Sha256, string[] MarkerFields);
    private sealed record ScanObservation(string Boundary, FileObservation[] Files);
    private sealed record RequestObservation(string Server, string Route, string[] MarkerFields);
    private static ScanObservation Scan(string root, string boundary, Dictionary<string, string> markers) => new(boundary,
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order().Select(path =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var bytes = memory.ToArray();
            var text = Encoding.UTF8.GetString(bytes);
            return new FileObservation(Path.GetRelativePath(root, path), bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)),
                markers.Where(m => text.Contains(m.Value, StringComparison.Ordinal)).Select(m => m.Key).ToArray());
        }).ToArray());

    private static async Task WaitAsync(Func<bool> condition, string invariant, TimeSpan? timeout = null)
    {
        var end = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(12));
        while (!condition() && DateTime.UtcNow < end) await Task.Delay(100);
        Program.Check(condition(), invariant);
    }

    private sealed class Engine : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
        private readonly string _secret = Guid.NewGuid().ToString("N");
        private readonly int _port;
        internal string Root { get; }
        internal string Session => Path.Combine(Root, "session.txt");
        private Engine(string binary, string root, string? input)
        {
            Root = root;
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); _port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var config = Path.Combine(root, "aria2.conf");
            File.WriteAllLines(config, ["enable-rpc=true", "rpc-listen-all=false", "rpc-allow-origin-all=false", $"rpc-listen-port={_port}", $"rpc-secret={_secret}",
                "no-netrc=true", "auto-save-interval=5", "max-tries=1", "connect-timeout=3", "timeout=10", "save-session-interval=5", $"save-session={Session}",
                "console-log-level=warn", "log-level=debug", $"log={Path.Combine(root, "engine.log")}", "all-proxy=", "http-proxy=", "https-proxy=", "check-certificate=false"]);
            var start = new ProcessStartInfo(binary) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--conf-path=" + config);
            if (input is not null) { start.ArgumentList.Add("--input-file=" + input); start.ArgumentList.Add("--pause=true"); }
            _process = Process.Start(start)!;
            // Console is intentionally discarded; verbose engine log is scanned independently.
            _process.BeginOutputReadLine(); _process.BeginErrorReadLine();
        }
        internal static async Task<Engine> StartAsync(string binary, string root, string? input = null)
        {
            var engine = new Engine(binary, root, input);
            try
            {
                for (var i = 0; i < 80; i++)
                {
                    try { await engine.RpcAsync("getVersion"); return engine; }
                    catch (HttpRequestException) { await Task.Delay(100); }
                }
                throw new InvalidOperationException("Engine did not become ready");
            }
            catch { await engine.DisposeAsync(); throw; }
        }
        internal async Task<JsonElement> RpcAsync(string method, params object[] args)
        {
            var parameters = new object[] { "token:" + _secret }.Concat(args).ToArray();
            using var response = await _http.PostAsync($"http://127.0.0.1:{_port}/jsonrpc", new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = "spike", method = "aria2." + method, @params = parameters }), Encoding.UTF8, "application/json"));
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Program.Check(!document.RootElement.TryGetProperty("error", out _), "Engine RPC rejected experiment");
            return document.RootElement.GetProperty("result").Clone();
        }
        internal async Task ShutdownAsync() { await RpcAsync("shutdown"); await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)); }
        internal void Kill() { if (!_process.HasExited) { _process.Kill(entireProcessTree: true); _process.WaitForExit(); } }
        public ValueTask DisposeAsync() { Kill(); _process.Dispose(); _http.Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class Origin : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<Task> _handlers = [];
        private readonly Task _loop;
        private readonly Dictionary<string, string> _markers;
        private readonly ConcurrentQueue<RequestObservation> _observations;
        private readonly string _name;
        private readonly string? _redirect;
        internal Origin(Dictionary<string, string> markers, ConcurrentQueue<RequestObservation> observations, string name, string? redirect = null)
        {
            _markers = markers; _observations = observations; _name = name; _redirect = redirect;
            _listener.Start(); _loop = AcceptAsync();
        }
        internal string Url(string path) => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}{path}";
        private async Task AcceptAsync()
        {
            try { while (!_stop.IsCancellationRequested) _handlers.Add(ServeAsync(await _listener.AcceptTcpClientAsync(_stop.Token))); }
            catch (Exception ex) when (_stop.IsCancellationRequested && ex is OperationCanceledException or SocketException) { }
        }
        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var first = await reader.ReadLineAsync(_stop.Token) ?? "";
                var headers = new List<string>(); string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(_stop.Token))) headers.Add(line);
                var route = first.Split(' ').ElementAtOrDefault(1) ?? "/";
                _observations.Enqueue(new(_name, route, _markers.Where(m => headers.Any(h => h.Contains(m.Value, StringComparison.Ordinal))).Select(m => m.Key).ToArray()));
                var routePath = route.Split('?')[0];
                var query = route.Contains('?') ? route[route.IndexOf('?')..] : "";
                if (routePath is "/same" or "/cross" or "/host")
                {
                    var location = (routePath == "/same" ? Url("/file") : routePath == "/host" ? _redirect!.Replace("127.0.0.1", "localhost", StringComparison.Ordinal) : _redirect) + query;
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 302 Found\r\nLocation: {location}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _stop.Token);
                }
                else
                {
                    var slow = route == "/slow";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {(slow ? 1048576 : 4)}\r\nConnection: close\r\n\r\n"), _stop.Token);
                    if (slow) for (var i = 0; i < 256; i++) { await stream.WriteAsync(new byte[4096], _stop.Token); await Task.Delay(100, _stop.Token); }
                    else await stream.WriteAsync("test"u8.ToArray(), _stop.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        }
        public async ValueTask DisposeAsync() { _stop.Cancel(); _listener.Stop(); await _loop; await Task.WhenAll(_handlers); _stop.Dispose(); }
    }

    // Python's OpenSSL fixture avoids Schannel's temporary private-key storage requirements.
    // It tunnels only decrypted synthetic traffic to our loopback HTTP source.
    private sealed class TlsProxy(Process process, int port) : IAsyncDisposable
    {
        internal string Url(string path) => $"https://127.0.0.1:{port}{path}";
        internal static async Task<TlsProxy> StartAsync(string root, int targetPort)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            var cert = Path.Combine(root, "synthetic-tls-cert.pem");
            var privateKey = Path.Combine(root, "synthetic-tls-key.pem");
            await File.WriteAllTextAsync(cert, certificate.ExportCertificatePem());
            await File.WriteAllTextAsync(privateKey, key.ExportPkcs8PrivateKeyPem());
            var start = new ProcessStartInfo("python") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-u", Path.Combine(AppContext.BaseDirectory, "fixtures", "credential-tls.py"), cert, privateKey, targetPort.ToString(System.Globalization.CultureInfo.InvariantCulture) }) start.ArgumentList.Add(arg);
            var process = Process.Start(start)!;
            process.BeginErrorReadLine();
            try
            {
                var first = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Program.Check(int.TryParse(first, out var port) && port > 0, "Synthetic TLS fixture ready");
                return new(process, port);
            }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); process.Dispose(); throw; }
        }
        public async ValueTask DisposeAsync() { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); process.Dispose(); }
    }
}
