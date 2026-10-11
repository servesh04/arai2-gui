using AriaGui.Application.Downloads;
using AriaGui.Application.Engine;
using AriaGui.Infrastructure.Aria2;
using AriaGui.Infrastructure.Downloads;
using AriaGui.Infrastructure.Persistence;

// Isolated owner process used by recovery tests. The parent kills this host, not a user app.
if (args.Length > 0 && args[0].StartsWith("--bridge-", StringComparison.Ordinal)) return await BrowserBridgeFixture.RunAsync(args);
if (args.Length == 6 && args[0] == "--recovery-host")
{
    var runtime = args[1]; var executable = args[2]; var port = int.Parse(args[3]); var url = args[4]; var mode = args[5];
    if (!new Uri(url).IsLoopback) throw new ArgumentException("Recovery fixture URLs must be local.");
    using var instance = new SingleInstanceLease(runtime);
    var repository = new SqliteDownloadRepository(Path.Combine(runtime, "downloads.db"));
    await using var manager = new Aria2ProcessManager(new(executable, runtime) { RpcPort = port }, repository);
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
    if (mode == "paused") await coordinator.PauseAsync(store.Current.Downloads[0].Job.Id);
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
