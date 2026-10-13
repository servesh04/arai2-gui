using AriaGui.Application.Downloads;
using AriaGui.Application.Engine;
using AriaGui.Infrastructure.Aria2;
using AriaGui.Infrastructure.Downloads;
using AriaGui.Infrastructure.Persistence;

// Isolated owner process used by recovery tests. The parent kills this host, not a user app.
if (args.Length > 0 && args[0].StartsWith("--bridge-", StringComparison.Ordinal)) return await BrowserBridgeFixture.RunAsync(args);
if (args.Length == 3 && args[0] == "--takeover-crash") return await PreparedTakeoverCrashFixture.RunAsync(args[1], args[2]);
if (args.Length == 2 && args[0] == "--gateway-runtime-probe")
{
    var runtime = Path.GetFullPath(args[1]); var allowed = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!runtime.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || Directory.Exists(runtime)) throw new ArgumentException("Use a fresh private child of the probe binary folder.");
    await using var gateway = new ContinuityLoopbackGateway(runtime, () => Environment.ProcessId, _ => new RuntimeOrigin(), true);
    await gateway.StartAsync(default);
    var address = await gateway.RegisterAsync(Guid.NewGuid(), Guid.NewGuid(), "https://example.invalid/runtime.bin", false, default);
    using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
    var bytes = await http.GetByteArrayAsync(address);
    if (!bytes.SequenceEqual(new byte[] { 1, 2, 3, 4 })) throw new IOException("Runtime probe bytes differ.");
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { runtime = Environment.Version.ToString(), bytes = bytes.Length,
        sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),
        kestrel = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == "Microsoft.AspNetCore.Server.Kestrel.Core").Location }));
    return 0;
}
if (args.Length == 6 && args[0] == "--recovery-host")
{
    var runtime = args[1]; var executable = args[2]; var port = int.Parse(args[3]); var url = args[4]; var mode = args[5];
    if (!new Uri(url).IsLoopback) throw new ArgumentException("Recovery fixture URLs must be local.");
    using var instance = new SingleInstanceLease(runtime);
    var repository = new SqliteDownloadRepository(Path.Combine(runtime, "downloads.db"));
    var guarded = mode.StartsWith("guarded-", StringComparison.Ordinal);
    if (guarded) mode = mode[8..];
    var options = new Aria2ProcessOptions(executable, runtime) { RpcPort = port, UseContinuityGateway = guarded,
        ContinuityTestOrigins = guarded ? source => new AriaGui.Infrastructure.Tests.ControlledLoopbackOrigin(source) : null };
    await using var manager = new Aria2ProcessManager(options, repository);
    await manager.StartAsync(default);
    var real = new ManagedAria2Client(manager);
    var store = new DownloadRuntimeStore();
    IAria2Client client = mode is "before-rpc" or "accepted" ? new BlockingClient(real, mode, manager.OwnedProcessId!.Value) : real;
    var coordinator = new DownloadCoordinator(client, store, TimeProvider.System, repository, new FileDownloadDestination());
    await coordinator.AddAsync(url, directory: Path.Combine(runtime, "downloads"), fileName: "recover.bin");
    while (store.Current.Downloads[0].Progress.DownloadedBytes == 0)
    {
        await coordinator.RefreshAsync();
        if (store.Current.Downloads[0].Progress.Status is AriaGui.Core.Downloads.DownloadStatus.Failed or AriaGui.Core.Downloads.DownloadStatus.Completed) break;
        await Task.Delay(100);
    }
    if (mode == "retried-paused")
    {
        var job = store.Current.Downloads[0].Job.Id;
        await coordinator.RemoveAsync(job); await coordinator.RefreshAsync(); await coordinator.RetryAsync(job);
        while (store.Current.Downloads[0].Progress.DownloadedBytes == 0)
        { await coordinator.RefreshAsync(); await Task.Delay(50); }
    }
    if (mode is "paused" or "retried-paused") await coordinator.PauseAsync(store.Current.Downloads[0].Job.Id);
    await coordinator.RefreshAsync();
    await manager.CheckpointAsync(default);
    Console.WriteLine($"READY {manager.OwnedProcessId}"); Console.Out.Flush();
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}
// Optional controlled HTTP fixture for the native download smoke check.
if (args.Contains("--http-fixture"))
{
    await using var server = new AriaGui.Infrastructure.Tests.LocalHttpServer();
    Console.WriteLine(server.Url);
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}
// The normal owned-child mode proves timeout/early-exit cleanup without opening a port.
if (Environment.GetEnvironmentVariable("ARIAGUI_FIXTURE_EXIT") == "1") return 42;
await Task.Delay(Timeout.InfiniteTimeSpan);
return 0;

sealed class BlockingClient(IAria2Client inner, string mode, int pid) : IAria2Client
{
    public async Task<EngineDownload> AddAsync(EngineDownloadRequest request, CancellationToken token)
    {
        if (mode == "accepted") await inner.AddAsync(request, token);
        Console.WriteLine($"READY {pid}"); Console.Out.Flush();
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        throw new InvalidOperationException();
    }
    public Task<IReadOnlyList<EngineDownload>> GetDownloadsAsync(IReadOnlyList<string> ids, CancellationToken token) => inner.GetDownloadsAsync(ids, token);
}
sealed class RuntimeOrigin : IContinuityOriginTransport
{
    public Task<HttpResponseMessage> OpenAsync(long offset, string? ifMatch, CancellationToken token)
    {
        if (offset != 0) throw new IOException("Probe does not reuse partials.");
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        { RequestMessage = new(HttpMethod.Get, "https://example.invalid/runtime.bin"), Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 }) };
        response.Headers.ETag = new("\"runtime\""); return Task.FromResult(response);
    }
}
