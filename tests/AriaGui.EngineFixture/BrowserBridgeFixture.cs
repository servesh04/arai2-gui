using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using AriaGui.Application.Downloads;
using AriaGui.Browser.Contracts;
using AriaGui.Core.Downloads;
using AriaGui.Infrastructure.Aria2;
using AriaGui.Infrastructure.Browser;
using AriaGui.Infrastructure.Downloads;
using AriaGui.Infrastructure.Persistence;
using Microsoft.Win32.SafeHandles;

internal static class BrowserBridgeFixture
{
    internal static async Task<int> RunAsync(string[] args)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            if (args[0] == "--bridge-fault-host") return await FaultAsync(args, deadline.Token);
            if (args[0] is "--bridge-client" or "--bridge-client-drop")
            {
                using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
                await pipe.ConnectAsync(deadline.Token);
                if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) || pid != uint.Parse(args[2])) return 4;
                var frames = new FrameConnection(pipe, FrameTransport.PipeLittleEndian);
                var request = await Console.In.ReadLineAsync(deadline.Token);
                await frames.WriteAsync(Encoding.UTF8.GetBytes(request!), deadline.Token);
                if (args[0] == "--bridge-client-drop") return 0;
                var response = await frames.ReadAsync(deadline.Token);
                if (response is null) return 5;
                Console.WriteLine(Encoding.UTF8.GetString(response)); return 0;
            }
            var root = Path.GetFullPath(args[1]);
            if (!File.Exists(Path.Combine(root, "synthetic-browser-bridge"))) return 6;
            using var lease = new SingleInstanceLease(root);
            if (!lease.IsPrimary) { Console.WriteLine("SECONDARY"); return 0; }
            var endpoint = BrowserBridgeEndpoint.Open(root, "fixture", "unpackaged");
            using var lifetime = new CancellationTokenSource();
            var preferences = new JsonUserPreferencesRepository(Path.Combine(root, "preferences.json"));
            var defaultFolder = Path.Combine(root, "downloads");
            if (!File.Exists(Path.Combine(root, "preferences.json"))) await preferences.SaveAsync(new(DefaultFolder: defaultFolder), deadline.Token);
            var repository = new SqliteDownloadRepository(Path.Combine(root, "downloads.db"));
            var engine = new FixtureEngine(); var coordinator = new DownloadCoordinator(engine, new(), TimeProvider.System, repository, new FileDownloadDestination());
            var preferenceService = new DownloadPreferencesService(preferences, defaultFolder);
            var intake = new ExternalDownloadIntakeService(coordinator, preferenceService, TimeProvider.System, lifetime.Token, new Offline());
            await using var server = new BrowserBridgeServer(intake, preferenceService, lease, endpoint, args.Length >= 3 ? args[2] : Environment.ProcessPath!, lifetime.Token);
            Console.WriteLine("STARTING"); Console.Out.Flush();
            var automatic = args[0] == "--bridge-auto-host";
            if (!automatic && await Console.In.ReadLineAsync(deadline.Token) != "START") return 7;
            await server.StartAsync(deadline.Token);
            Console.WriteLine(JsonSerializer.Serialize(new { endpoint.InstallationId, endpoint.PipeName, Pid = Environment.ProcessId })); Console.Out.Flush();
            if (automatic)
            {
                Console.WriteLine("synthetic_gui_noise"); Console.Error.WriteLine("synthetic_gui_noise");
                while (!File.Exists(Path.Combine(root, "bridge-auto-stop")) && !deadline.IsCancellationRequested) await Task.Delay(50);
            }
            while (!automatic && await Console.In.ReadLineAsync(deadline.Token) is { } command)
            {
                if (command == "STATS") { Console.WriteLine($"STATS {engine.Adds} {(await repository.LoadAsync(deadline.Token)).Count}"); Console.Out.Flush(); }
                else if (command == "TRAY") { Console.WriteLine("HIDDEN_SIMULATION"); Console.Out.Flush(); }
                else if (command == "STOP") { server.StopAdmission(); Console.WriteLine("BARRIER"); Console.Out.Flush(); }
                else if (command == "EXIT") break;
            }
            server.StopAdmission(); lifetime.Cancel();
            using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await server.DrainAsync(drain.Token);
            Console.WriteLine("CLOSED"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("bridge_fixture_error:" + ex.GetType().Name); return 9; }
    }
    private static async Task<int> FaultAsync(string[] args, CancellationToken token)
    {
        var root = Path.GetFullPath(args[1]); if (!File.Exists(Path.Combine(root,"synthetic-browser-bridge"))) return 6;
        using var lease = new SingleInstanceLease(root); if (!lease.IsPrimary) return 7;
        var endpoint = BrowserBridgeEndpoint.Open(root,"fixture","unpackaged");
        using var pipe = new NamedPipeServerStream(endpoint.PipeName, PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Console.WriteLine("FAULT_READY"); Console.Out.Flush(); await pipe.WaitForConnectionAsync(token);
        var frames = new FrameConnection(pipe, FrameTransport.PipeLittleEndian);
        var hello = ContractJson.ParseRequest((await frames.ReadAsync(token))!).Value!;
        await frames.WriteAsync(ContractJson.Serialize(new ResponseEnvelope(new(1,0),hello.RequestId,new(ResultCode.Ready,AppVersion:args[3]=="incompatible"?"2.0.0":"1.0.0",
            Capabilities:[Capability.AddPublicGet,Capability.GetRequestStatus],MaxMessageBytes:ProtocolLimits.MaximumMessageBytes))),token);
        if(args[3]=="incompatible"){await frames.ReadAsync(token);return 0;}
        var add = ContractJson.ParseRequest((await frames.ReadAsync(token))!).Value!;
        if(add.Action!=BrowserAction.AddPublicGet)return 8;
        File.WriteAllText(Path.Combine(root,"forwarded-add"),"observed");
        if(args[3]=="wrong-response-id") await frames.WriteAsync(ContractJson.Serialize(new ResponseEnvelope(new(1,0),Guid.NewGuid(),new(ResultCode.Accepted,
            JobId:Guid.NewGuid(),State:JobIntakeState.Queued,Duplicate:false))),token);
        return 0;
    }
    private sealed class Offline : IExternalDispatchReadiness
    { public Task WaitForReadyAsync(CancellationToken token) => Task.Delay(Timeout.InfiniteTimeSpan, token); }
    private sealed class FixtureEngine : IAria2Client
    {
        internal int Adds;
        public Task<EngineDownload> AddAsync(EngineDownloadRequest request, CancellationToken token)
        { Interlocked.Increment(ref Adds); return Task.FromResult(new EngineDownload(request.EngineId, new(DownloadStatus.Queued, 0, null, 0))); }
        public Task<IReadOnlyList<EngineDownload>> GetDownloadsAsync(IReadOnlyList<string> ids, CancellationToken token) => Task.FromResult<IReadOnlyList<EngineDownload>>([]);
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle handle, out uint pid);
}
