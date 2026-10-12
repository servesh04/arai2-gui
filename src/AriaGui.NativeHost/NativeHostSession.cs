using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using AriaGui.Browser.Contracts;
using Microsoft.Win32.SafeHandles;

namespace AriaGui.NativeHost;

public sealed class NativeHostSession : IAsyncDisposable
{
    private readonly InstallationDescriptor _descriptor;
    private NamedPipeClientStream? _pipe;
    private FrameConnection? _frames;
    private Process? _launched;
    private int _connections, _pipeRequests;
    private bool _launchAttempted;
    private NativeHostSession(InstallationDescriptor descriptor) => _descriptor = descriptor;
    public static async Task<int> RunAsync(string[] args)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var input = Console.OpenStandardInput(); using var output = Console.OpenStandardOutput();
        var reads = new FrameConnection(input, FrameTransport.NativeMessagingWindowsX64);
        var writes = new FrameConnection(output, FrameTransport.NativeMessagingWindowsX64);
        NativeHostSession? session = null;
        try
        {
            for (var count = 0; count < 16; count++)
            {
                // Console streams need not honor cancellation. Bound the await; exiting this process abandons any blocked read.
                var body = await Task.Run(async () => await reads.ReadAsync(lifetime.Token)).WaitAsync(TimeSpan.FromSeconds(5), lifetime.Token);
                if (body is null) return 0;
                var parsed = ContractJson.ParseRequest(body);
                if (!parsed.Success)
                {
                    if (parsed.CorrelationId is { } id) await WriteAsync(writes, id, new(ResultCode.Rejected, Reason: parsed.Error), lifetime.Token);
                    Diagnostic("invalid_contract"); return 2;
                }
                if (session is null)
                {
                    try
                    {
                        var descriptor = InstallationDescriptor.Load(); _ = BrowserInvocation.Validate(args, descriptor);
                        session = new(descriptor);
                    }
                    catch { await WriteAsync(writes, parsed.Value!.RequestId, new(ResultCode.Unavailable), lifetime.Token); Diagnostic("installation_or_caller"); return 3; }
                }
                var result = await session.RelayAsync(parsed.Value!, body, lifetime.Token);
                await WriteAsync(writes, parsed.Value!.RequestId, result, lifetime.Token);
            }
            Diagnostic("session_limit"); return 0;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException)
        { Diagnostic("session_closed"); return 4; }
        catch { Diagnostic("host_failure"); return 5; }
        finally { if (session is not null) await session.DisposeAsync(); }
    }
    private static async Task WriteAsync(FrameConnection writes, Guid id, ResponseResult result, CancellationToken token) =>
        await Task.Run(async () => await writes.WriteAsync(ContractJson.Serialize(new ResponseEnvelope(new(1, 0), id, result)), token))
            .WaitAsync(TimeSpan.FromSeconds(5), token);
    private async Task<ResponseResult> RelayAsync(RequestEnvelope request, byte[] body, CancellationToken lifetime)
    {
        var installation = request.Payload switch { HelloPayload hello => hello.InstallationId, PublicGetPayload add => add.InstallationId,
            StatusPayload status => status.InstallationId, _ => Guid.Empty };
        if (installation != _descriptor.InstallationId) return new(ResultCode.Rejected, Reason: RejectionReason.InvalidRequest);
        var mutationMayHaveBeenSent = false;
        try
        {
            if (_pipeRequests >= 7) Disconnect(); // Server limit is eight including the internal handshake.
            if (_pipe is null) await ConnectAsync(lifetime);
            using var responseDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime); responseDeadline.CancelAfter(TimeSpan.FromSeconds(10));
            // Mark BEFORE the first write: a partial/failed write cannot prove the app received no mutation.
            mutationMayHaveBeenSent = request.Action == BrowserAction.AddPublicGet;
            await _frames!.WriteAsync(body, responseDeadline.Token); _pipeRequests++;
            var response = await _frames.ReadAsync(responseDeadline.Token);
            if (response is null) throw new IOException();
            var parsed = ContractJson.ParseResponse(response);
            if (!parsed.Success || parsed.Value!.RequestId != request.RequestId) throw new IOException();
            return parsed.Value.Result;
        }
        catch
        {
            Disconnect(); Diagnostic(mutationMayHaveBeenSent ? "outcome_unknown" : "app_unavailable");
            return new(mutationMayHaveBeenSent ? ResultCode.Unknown : ResultCode.Unavailable);
        }
    }
    private async Task ConnectAsync(CancellationToken lifetime)
    {
        if (++_connections > 3) throw new IOException();
        using var cold = CancellationTokenSource.CreateLinkedTokenSource(lifetime); cold.CancelAfter(TimeSpan.FromSeconds(15));
        var endpoint = BridgeEndpointName.Create(_descriptor.RuntimeDirectory, _descriptor.Channel, _descriptor.PackageModel,
            WindowsSid(), _descriptor.InstallationId);
        if (!await TryConnectAsync(endpoint, TimeSpan.FromSeconds(2), cold.Token))
        {
            if (!_descriptor.AllowLaunch || _launchAttempted) throw new IOException();
            _launchAttempted = true;
            // Recheck target integrity immediately before the single fixed launch; no request data enters arguments.
            InstallationDescriptor.VerifyPrivateFile(_descriptor.GuiExecutable, 256 * 1024 * 1024);
            using (var source = File.OpenRead(_descriptor.GuiExecutable))
                if (!Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source)).Equals(_descriptor.GuiSha256, StringComparison.OrdinalIgnoreCase)) throw new IOException();
            var start = new ProcessStartInfo(_descriptor.GuiExecutable) { UseShellExecute = false, CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(_descriptor.GuiExecutable)!, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in _descriptor.LaunchArguments) start.ArgumentList.Add(argument);
            _launched = Process.Start(start) ?? throw new IOException();
            _ = DiscardAsync(_launched.StandardOutput.BaseStream); _ = DiscardAsync(_launched.StandardError.BaseStream);
            if (!await TryConnectAsync(endpoint, TimeSpan.FromSeconds(13), cold.Token)) { Diagnostic("cold_connect"); throw new IOException(); }
        }
        if (!VerifyServer(_pipe!)) { Diagnostic("pipe_identity"); Disconnect(); throw new IOException(); }
        _frames = new(_pipe!, FrameTransport.PipeLittleEndian); _pipeRequests = 0;
        var helloId = Guid.NewGuid();
        await _frames.WriteAsync(ContractJson.Serialize(new RequestEnvelope(new(1, 0), helloId, BrowserAction.Hello,
            new HelloPayload(_descriptor.InstallationId, "1.0.0"))), cold.Token);
        var response = await _frames.ReadAsync(cold.Token);
        var ready = response is null ? null : ContractJson.ParseResponse(response).Value;
        if (ready?.RequestId != helloId || ready.Result.Code != ResultCode.Ready
            || !Version.TryParse(ready.Result.AppVersion, out var version) || version.Major != 1 || version < Version.Parse(_descriptor.MinimumAppVersion)
            || ready.Result.MaxMessageBytes != ProtocolLimits.MaximumMessageBytes
            || ready.Result.Capabilities is null || !ready.Result.Capabilities.Contains(Capability.AddPublicGet) || !ready.Result.Capabilities.Contains(Capability.GetRequestStatus))
        { Diagnostic("handshake"); throw new IOException(); }
    }
    private async Task<bool> TryConnectAsync(string endpoint, TimeSpan budget, CancellationToken lifetime)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime); deadline.CancelAfter(budget);
        for (var attempt = 0; attempt < 16 && !deadline.IsCancellationRequested; attempt++)
        {
            var candidate = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
            try
            {
                using var slice = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token); slice.CancelAfter(TimeSpan.FromMilliseconds(250));
                await candidate.ConnectAsync(slice.Token); _pipe = candidate; return true;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException) { candidate.Dispose(); }
            try { await Task.Delay(Math.Min(100 << Math.Min(attempt, 3), 800), deadline.Token); } catch (OperationCanceledException) { break; }
        }
        return false;
    }
    private bool VerifyServer(NamedPipeClientStream pipe)
    {
        try
        {
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) || pid == 0) return false;
            using var process = Process.GetProcessById(checked((int)pid));
            if (!OperatingSystem.IsWindows() || !OpenProcessToken(process.Handle, 8, out var token)) return false;
            using (token) using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                if (identity.User?.Value != WindowsSid()) return false;
            var executable = process.MainModule?.FileName;
            if (executable is null || !string.Equals(Path.GetFullPath(executable), Path.GetFullPath(_descriptor.GuiExecutable), StringComparison.OrdinalIgnoreCase)) return false;
            using var source = File.OpenRead(executable);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source)).Equals(_descriptor.GuiSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
    private static string WindowsSid()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return WindowsIdentity.GetCurrent().User?.Value ?? throw new IOException();
    }
    private static async Task DiscardAsync(Stream stream)
    {
        try { var buffer = new byte[4096]; var total = 0; while (total < 65536) { var read = await stream.ReadAsync(buffer); if (read == 0) break; total += read; } }
        catch { } // Child diagnostics must never enter host stdout/stderr or unbounded buffers.
    }
    private void Disconnect() { _pipe?.Dispose(); _pipe = null; _frames = null; _pipeRequests = 0; }
    private static void Diagnostic(string code) => Console.Error.WriteLine("native_host:" + code);
    public ValueTask DisposeAsync() { Disconnect(); _launched?.Dispose(); return ValueTask.CompletedTask; }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle handle, out uint pid);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
}
