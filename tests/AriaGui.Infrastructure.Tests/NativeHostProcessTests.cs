using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using AriaGui.Browser.Contracts;
using AriaGui.Infrastructure.Aria2;
using AriaGui.Infrastructure.Browser;

namespace AriaGui.Infrastructure.Tests;

public sealed class NativeHostProcessTests
{
    [Fact]
    public async Task TenNativeHostsColdStartConvergeThroughOnePrimaryApplicationLease()
    {
        using var fixture = new Fixture(launch: true); var hosts = Enumerable.Range(0,10).Select(_=>fixture.Host()).ToArray(); var request=fixture.Add(Guid.NewGuid());
        await Task.WhenAll(hosts.Select(host=>fixture.WriteAsync(host,request)));
        var receipts=await Task.WhenAll(hosts.Select(async host=>{var receipt=await fixture.ResponseAsync(host);host.StandardInput.Close();await fixture.ExitAsync(host,0);return receipt;}));
        Assert.All(receipts,response=>Assert.Equal(ResultCode.Accepted,response.Result.Code));
        Assert.Single(receipts.Select(response=>response.Result.JobId).Distinct());
        var repository=new AriaGui.Infrastructure.Persistence.SqliteDownloadRepository(Path.Combine(fixture.Runtime,"downloads.db"));
        Assert.Single(await repository.LoadAsync(default));
        File.WriteAllText(Path.Combine(fixture.Runtime,"bridge-auto-stop"),"stop");await fixture.WaitAutoGuiExitAsync();
    }
    [Fact]
    public async Task Exact64KiBNativeBodyIsAcceptedAndMissingDescriptorIsUnavailable()
    {
        using var fixture=new Fixture();await fixture.StartGuiAsync();var host=fixture.Host();var hello=ContractJson.Serialize(fixture.Hello());
        var body=Enumerable.Repeat((byte)' ',ProtocolLimits.MaximumMessageBytes).ToArray();hello.CopyTo(body,0);
        await host.StandardInput.BaseStream.WriteAsync(Frame(body));await host.StandardInput.BaseStream.FlushAsync();Assert.Equal(ResultCode.Ready,(await fixture.ResponseAsync(host)).Result.Code);
        host.StandardInput.Close();await fixture.ExitAsync(host,0);
        File.Delete(Path.Combine(fixture.HostDirectory,"native-host.v1.json"));var missing=fixture.Host();await fixture.WriteAsync(missing,fixture.Hello());
        Assert.Equal(ResultCode.Unavailable,(await fixture.ResponseAsync(missing)).Result.Code);await fixture.ExitAsync(missing,3);
    }
    [Fact]
    public async Task BinaryPartialAndConcatenatedFramesRelayHelloAddAndScopedStatusWithoutEcho()
    {
        using var fixture = new Fixture(); await fixture.StartGuiAsync(); var host = fixture.Host();
        var id = Guid.NewGuid(); var add = fixture.Add(id);
        var hello = fixture.Hello(); var first = Frame(ContractJson.Serialize(hello));
        foreach (var part in first.Chunk(3)) await host.StandardInput.BaseStream.WriteAsync(part);
        await host.StandardInput.BaseStream.FlushAsync(); Assert.Equal(ResultCode.Ready, (await fixture.ResponseAsync(host)).Result.Code);
        var status = new RequestEnvelope(new(1, 0), Guid.NewGuid(), BrowserAction.GetRequestStatus, new StatusPayload(fixture.Installation, id));
        await host.StandardInput.BaseStream.WriteAsync(Frame(ContractJson.Serialize(add)).Concat(Frame(ContractJson.Serialize(status))).ToArray());
        await host.StandardInput.BaseStream.FlushAsync(); var accepted = await fixture.ResponseAsync(host); var queried = await fixture.ResponseAsync(host);
        Assert.Equal(ResultCode.Accepted, accepted.Result.Code); Assert.Equal(accepted.Result.JobId, queried.Result.JobId);
        host.StandardInput.Close(); await fixture.ExitAsync(host, 0); Assert.DoesNotContain("SIGNED_SECRET", await host.StandardError.ReadToEndAsync());
        Assert.Equal("STATS 0 1", await fixture.GuiCommandAsync("STATS"));
    }
    [Theory]
    [InlineData("chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/", "--parent-window=0")]
    [InlineData("chrome-extension://bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb/", "--parent-window=123456")]
    public async Task ChromiumConfiguredChromeAndEdgeOriginsAreAccepted(string origin, string parent)
    {
        using var fixture = new Fixture(); await fixture.StartGuiAsync(); var host = fixture.Host(origin, parent);
        await fixture.WriteAsync(host, fixture.Hello()); Assert.Equal(ResultCode.Ready, (await fixture.ResponseAsync(host)).Result.Code);
        host.StandardInput.Close(); await fixture.ExitAsync(host, 0);
    }
    [Fact]
    public async Task FirefoxRequiresExactManifestAndAddonId()
    {
        using var fixture = new Fixture(); await fixture.StartGuiAsync(); var valid = fixture.Host(fixture.FirefoxManifest, "ariagui@example.test");
        await fixture.WriteAsync(valid, fixture.Hello()); Assert.Equal(ResultCode.Ready, (await fixture.ResponseAsync(valid)).Result.Code); valid.StandardInput.Close(); await fixture.ExitAsync(valid, 0);
        var invalid = fixture.Host(fixture.FirefoxManifest + ".other", "ariagui@example.test");
        await fixture.WriteAsync(invalid, fixture.Hello()); Assert.Equal(ResultCode.Unavailable, (await fixture.ResponseAsync(invalid)).Result.Code); await fixture.ExitAsync(invalid, 3);
    }
    [Theory]
    [InlineData("chrome-extension://cccccccccccccccccccccccccccccccc/", "--parent-window=0")]
    [InlineData("chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/", "--parent-window=-1")]
    [InlineData("chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/", "--parent-window=0", "extra")]
    [InlineData("chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/")]
    public async Task InvalidBrowserArgumentsFailBeforeGuiMutation(params string[] args)
    {
        using var fixture = new Fixture(); var host = fixture.Host(args); await fixture.WriteAsync(host, fixture.Add(Guid.NewGuid()));
        Assert.Equal(ResultCode.Unavailable, (await fixture.ResponseAsync(host)).Result.Code); await fixture.ExitAsync(host, 3);
        Assert.False(File.Exists(Path.Combine(fixture.Runtime, "downloads.db")));
    }
    [Fact]
    public async Task MissingWarmAppWithoutLaunchIsUnavailableAndWrongInstallationIsRejected()
    {
        using var fixture = new Fixture(); var host = fixture.Host(); await fixture.WriteAsync(host, fixture.Hello());
        Assert.Equal(ResultCode.Unavailable, (await fixture.ResponseAsync(host)).Result.Code); host.StandardInput.Close(); await fixture.ExitAsync(host, 0);
        var other = fixture.Host(); await fixture.WriteAsync(other, new(new(1, 0), Guid.NewGuid(), BrowserAction.Hello, new HelloPayload(Guid.NewGuid(), "1.0.0")));
        Assert.Equal(ResultCode.Rejected, (await fixture.ResponseAsync(other)).Result.Code); other.StandardInput.Close(); await fixture.ExitAsync(other, 0);
    }
    [Fact]
    public async Task ColdLaunchUsesOnlyFixedSyntheticArgumentsAndDoesNotForwardChildNoise()
    {
        using var fixture = new Fixture(launch: true); var host = fixture.Host(); await fixture.WriteAsync(host, fixture.Add(Guid.NewGuid()));
        Assert.Equal(ResultCode.Accepted, (await fixture.ResponseAsync(host)).Result.Code); host.StandardInput.Close(); await fixture.ExitAsync(host, 0);
        Assert.DoesNotContain("synthetic_gui_noise", await host.StandardError.ReadToEndAsync());
        File.WriteAllText(Path.Combine(fixture.Runtime, "bridge-auto-stop"), "stop");
        await fixture.WaitAutoGuiExitAsync();
    }
    [Theory]
    [InlineData("drop", ResultCode.Unknown)]
    [InlineData("wrong-response-id", ResultCode.Unknown)]
    [InlineData("incompatible", ResultCode.Unavailable)]
    public async Task AfterForwardLossIsUnknownAndHandshakeIncompatibilityIsUnavailable(string mode, ResultCode expected)
    {
        using var fixture = new Fixture(); await fixture.StartFaultGuiAsync(mode); var host = fixture.Host(); await fixture.WriteAsync(host, fixture.Add(Guid.NewGuid()));
        Assert.Equal(expected, (await fixture.ResponseAsync(host)).Result.Code); host.StandardInput.Close(); await fixture.ExitAsync(host, 0);
        Assert.Equal(expected == ResultCode.Unknown, File.Exists(Path.Combine(fixture.Runtime, "forwarded-add")));
    }
    [Fact]
    public async Task SquattedPipeWrongServerExecutableFailsBeforeHandshakeOrMutation()
    {
        using var fixture = new Fixture(); using var squat = new NamedPipeServerStream(fixture.Endpoint.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var waiting = squat.WaitForConnectionAsync(); var host = fixture.Host(); await fixture.WriteAsync(host, fixture.Add(Guid.NewGuid())); await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ResultCode.Unavailable, (await fixture.ResponseAsync(host)).Result.Code); host.StandardInput.Close(); await fixture.ExitAsync(host, 0);
        var buffer = new byte[1]; Assert.Equal(0, await squat.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
    }
    [Theory]
    [InlineData("hash")][InlineData("broad-acl")][InlineData("duplicate")]
    public async Task DescriptorIntegrityAndPermissionsFailClosed(string defect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var fixture = new Fixture(); var descriptor = Path.Combine(fixture.HostDirectory, "native-host.v1.json");
        if (defect == "hash") File.WriteAllText(descriptor, File.ReadAllText(descriptor).Replace(fixture.GuiHash, new string('0', 64)));
        if (defect == "duplicate") File.WriteAllText(descriptor, File.ReadAllText(descriptor).Replace("{", "{\"SchemaVersion\":1,", StringComparison.Ordinal));
        if (defect == "broad-acl") { var file = new FileInfo(descriptor); var acl = file.GetAccessControl(); acl.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Write, AccessControlType.Allow)); file.SetAccessControl(acl); }
        var host = fixture.Host(); await fixture.WriteAsync(host, fixture.Hello()); Assert.Equal(ResultCode.Unavailable, (await fixture.ResponseAsync(host)).Result.Code); await fixture.ExitAsync(host, 3);
    }
    [Theory]
    [InlineData(0)][InlineData(65537)][InlineData(-1)]
    public async Task InvalidLengthOrUtf8ProducesNoStdoutAndNoDescriptorAccess(int length)
    {
        using var fixture = new Fixture(); var host = fixture.Host(); var bytes = length < 0 ? new byte[] { 1, 0, 0, 0, 255 } : new byte[4];
        if (length >= 0) BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)length);
        await host.StandardInput.BaseStream.WriteAsync(bytes); host.StandardInput.Close(); await fixture.ExitAsync(host, 4);
        Assert.Equal("", await host.StandardOutput.ReadToEndAsync());
    }
    [Fact]
    public async Task CleanEofAndPartialHeaderDeadlineAreBounded()
    {
        using var fixture = new Fixture(); var eof = fixture.Host(); eof.StandardInput.Close(); await fixture.ExitAsync(eof, 0); Assert.Equal("", await eof.StandardOutput.ReadToEndAsync());
        var stalled = fixture.Host(); await stalled.StandardInput.BaseStream.WriteAsync(new byte[] { 2 }); await stalled.StandardInput.BaseStream.FlushAsync();
        await fixture.ExitAsync(stalled, 4); Assert.Equal("", await stalled.StandardOutput.ReadToEndAsync());
    }
    [Fact]
    public async Task SensitiveForbiddenPayloadGetsOnlyClosedRejectionAndMajorMismatchNeverForwards()
    {
        using var fixture = new Fixture(); var host = fixture.Host(); var body = Encoding.UTF8.GetString(ContractJson.Serialize(fixture.Add(Guid.NewGuid()))).Replace("\"trigger\":", "\"headers\":{\"Cookie\":\"COOKIE_SECRET\"},\"trigger\":");
        await host.StandardInput.BaseStream.WriteAsync(Frame(Encoding.UTF8.GetBytes(body))); await host.StandardInput.BaseStream.FlushAsync();
        Assert.Equal(ResultCode.Rejected, (await fixture.ResponseAsync(host)).Result.Code); await fixture.ExitAsync(host, 2); Assert.DoesNotContain("COOKIE_SECRET", await host.StandardError.ReadToEndAsync());
        Assert.False(File.Exists(Path.Combine(fixture.Runtime, "downloads.db")));
        var major=fixture.Host();var mismatch=Encoding.UTF8.GetString(ContractJson.Serialize(fixture.Hello())).Replace("\"major\":1","\"major\":2");
        await major.StandardInput.BaseStream.WriteAsync(Frame(Encoding.UTF8.GetBytes(mismatch)));await major.StandardInput.BaseStream.FlushAsync();
        Assert.Equal(RejectionReason.UnsupportedProtocol,(await fixture.ResponseAsync(major)).Result.Reason);await fixture.ExitAsync(major,2);
    }
    private static byte[] Frame(byte[] body) { var frame = new byte[4 + body.Length]; BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length); body.CopyTo(frame,4); return frame; }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "AriaGUI.HostTests", Guid.NewGuid().ToString("N"));
        internal string Runtime => Path.Combine(Root, "runtime"); internal string HostDirectory => Path.Combine(Root, "host");
        internal string HostExecutable => Path.Combine(HostDirectory, "AriaGui.NativeHost.exe"); internal string GuiExecutable => Path.Combine(Root, "gui", "AriaGui.EngineFixture.exe");
        internal string FirefoxManifest => Path.Combine(HostDirectory, "firefox.native.json"); internal string GuiHash => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(GuiExecutable)));
        internal BrowserBridgeEndpoint Endpoint { get; } internal Guid Installation => Endpoint.InstallationId;
        private readonly List<Process> _children = []; private Process? _gui;
        internal Fixture(bool launch = false)
        {
            UserRuntimeDirectory.EnsurePrivate(Root); Directory.CreateDirectory(Runtime); File.WriteAllText(Path.Combine(Runtime, "synthetic-browser-bridge"), "synthetic");
            Copy(Path.Combine(AppContext.BaseDirectory,"NativeHostFixture"), HostDirectory); Copy(Path.Combine(AppContext.BaseDirectory,"Fixtures"), Path.Combine(Root,"gui"));
            Endpoint = BrowserBridgeEndpoint.Open(Runtime,"fixture","unpackaged");
            File.WriteAllText(FirefoxManifest, JsonSerializer.Serialize(new { name="com.ariagui.verification", type="stdio", path=HostExecutable, allowed_extensions=new[]{"ariagui@example.test"} }));
            File.WriteAllText(Path.Combine(HostDirectory,"native-host.v1.json"), JsonSerializer.Serialize(new { SchemaVersion=1, Mode="verification", InstallationId=Installation,
                Channel="fixture", PackageModel="unpackaged", RuntimeDirectory=Runtime, HostExecutable, GuiExecutable, GuiSha256=GuiHash, MinimumAppVersion="1.0.0", AllowLaunch=launch,
                LaunchArguments=new[]{"--bridge-auto-host",Runtime,HostExecutable}, ChromiumOrigins=new[]{"chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/","chrome-extension://bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb/"}, FirefoxManifest, FirefoxAddonIds=new[]{"ariagui@example.test"} }));
        }
        private static void Copy(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach(var file in Directory.GetFiles(from,"*",SearchOption.AllDirectories))
            { var target=Path.Combine(to,Path.GetRelativePath(from,file));Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(file,target);File.SetAttributes(target,FileAttributes.Normal); }
        }
        private Process Spawn(string executable, params string[] args)
        {
            var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true}; foreach(var arg in args)start.ArgumentList.Add(arg);
            var child=Process.Start(start)!; _children.Add(child); return child;
        }
        internal Process Host(params string[] args) => Spawn(HostExecutable,args.Length==0?new[]{"chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/","--parent-window=0"}:args);
        internal RequestEnvelope Hello() => new(new(1,0),Guid.NewGuid(),BrowserAction.Hello,new HelloPayload(Installation,"1.0.0"));
        internal RequestEnvelope Add(Guid id) => new(new(1,0),id,BrowserAction.AddPublicGet,new PublicGetPayload(Installation,DateTimeOffset.UtcNow,"https://example.invalid/資料?sig=SIGNED_SECRET%41%7e%2f&b=2&a=1","資料.zip"));
        internal async Task StartGuiAsync(){_gui=Spawn(GuiExecutable,"--bridge-host",Runtime,HostExecutable);Assert.Equal("STARTING",await LineAsync(_gui));await _gui.StandardInput.WriteLineAsync("START");await LineAsync(_gui);}
        internal async Task StartFaultGuiAsync(string mode){_gui=Spawn(GuiExecutable,"--bridge-fault-host",Runtime,HostExecutable,mode);Assert.Equal("FAULT_READY",await LineAsync(_gui));}
        internal async Task<string> GuiCommandAsync(string command){await _gui!.StandardInput.WriteLineAsync(command);return await LineAsync(_gui);}
        private static async Task<string> LineAsync(Process process)=>(await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8)))??throw new IOException("Synthetic process exited early.");
        internal async Task WriteAsync(Process host,RequestEnvelope request){await host.StandardInput.BaseStream.WriteAsync(Frame(ContractJson.Serialize(request)));await host.StandardInput.BaseStream.FlushAsync();}
        internal async Task<ResponseEnvelope> ResponseAsync(Process host){var frames=new FrameConnection(host.StandardOutput.BaseStream,FrameTransport.NativeMessagingWindowsX64,TimeSpan.FromSeconds(20));var body=await frames.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20));return ContractJson.ParseResponse(body!).Value??throw new IOException("Invalid host response.");}
        internal async Task ExitAsync(Process host,int code){await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));Assert.Equal(code,host.ExitCode);Assert.Equal("",await host.StandardOutput.ReadToEndAsync());}
        internal async Task WaitAutoGuiExitAsync(){for(var retry=0;retry<40;retry++){try{using var lease=new SingleInstanceLease(Runtime);if(lease.IsPrimary)return;}catch{}await Task.Delay(50);}throw new TimeoutException("Synthetic GUI did not exit.");}
        public void Dispose()
        {
            File.WriteAllText(Path.Combine(Runtime,"bridge-auto-stop"),"stop");
            if(_gui is not null)try{if(!_gui.HasExited){_gui.StandardInput.WriteLine("EXIT");_gui.WaitForExit(1000);}}catch(IOException){}catch(InvalidOperationException){}
            foreach(var child in _children){try{if(!child.HasExited){child.Kill(true);child.WaitForExit(3000);}}catch(InvalidOperationException){}child.Dispose();}
            var prefix=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"AriaGUI.HostTests"))+Path.DirectorySeparatorChar;
            if(!Path.GetFullPath(Root).StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException();
            for(var retry=0;retry<10;retry++)
            {
                try{Directory.Delete(Root,true);return;}
                catch(Exception ex)when((ex is IOException or UnauthorizedAccessException)&&retry<9){Thread.Sleep(100);}
            }
        }
    }
}
