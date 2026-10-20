using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using AriaGui.Application.Downloads;
using AriaGui.Browser.Contracts;
using Microsoft.Win32.SafeHandles;

namespace AriaGui.NativeHost;

public static class HandoffNativeSession
{
    private const int BrowserLimit = 1024 * 1024;
    private static readonly byte[] Unavailable = new BrowserHandoffResponse(false, Error: "app-unavailable").ToJson();
    private static readonly byte[] Invalid = new BrowserHandoffResponse(false, Error: "invalid-request").ToJson();

    public static async Task<int> RunAsync(string[] args)
    {
        var origin = "chrome-extension://" + HandoffRegistration.ExtensionId + "/";
        if (!OperatingSystem.IsWindows() || args.Length is < 1 or > 2 || args[0] != origin
            || args.Length == 2 && (!args[1].StartsWith("--parent-window=", StringComparison.Ordinal)
                || !ulong.TryParse(args[1].AsSpan(16), NumberStyles.None, CultureInfo.InvariantCulture, out _))) return 2;
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        try
        {
            while (true)
            {
                var request = await ReadFrameAsync(input, BrowserLimit);
                if (request is null) return 0;
                var reply = request.Length > BrowserHandoffProtocol.MaximumPipeBytes
                    ? Invalid : await ForwardAsync(request);
                await WriteFrameAsync(output, reply);
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or JsonException) { return 1; }
    }

    private static async Task<byte[]> ForwardAsync(byte[] body)
    {
        if (!OperatingSystem.IsWindows()) return Unavailable;
        var appPath = HandoffRegistration.RegisteredAppPath();
        if (appPath is null || !Path.IsPathFullyQualified(appPath) || !File.Exists(appPath)) return Unavailable;
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (sid is null) return Unavailable;
        var pipeName = BrowserHandoffProtocol.PipeName(sid);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var launched = false;
        while (!deadline.IsCancellationRequested)
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
                TokenImpersonationLevel.Impersonation);
            var sent = false;
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                attempt.CancelAfter(TimeSpan.FromMilliseconds(300));
                await pipe.ConnectAsync(attempt.Token);
                if (!ServerIsApp(pipe, appPath)) return Unavailable;
                await WriteFrameAsync(pipe, body, deadline.Token);
                sent = true;
                var response = await ReadFrameAsync(pipe, BrowserHandoffProtocol.MaximumPipeBytes, deadline.Token);
                if (response is null || !ValidResponse(response)) return Unavailable;
                try { await pipe.WriteAsync(new byte[] { 1 }, deadline.Token); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
                return response;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException)
            {
                // Once delivered, retrying can add the same download twice while the browser keeps its copy.
                if (sent) return Unavailable;
            }
            if (!launched)
            {
                launched = true;
                try
                {
                    var launch = new ProcessStartInfo(appPath)
                    {
                        UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                        WorkingDirectory = Path.GetDirectoryName(appPath)!
                    };
                    launch.ArgumentList.Add(GuiLaunchContract.BrowserWakeArgument);
                    using var process = Process.Start(launch);
                }
                catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { return Unavailable; }
            }
            try { await Task.Delay(100, deadline.Token); } catch (OperationCanceledException) { break; }
        }
        return Unavailable;
    }

    private static bool ValidResponse(byte[] response)
    {
        try
        {
            using var parsed = JsonDocument.Parse(response);
            return parsed.RootElement.ValueKind == JsonValueKind.Object
                && parsed.RootElement.TryGetProperty("ok", out var ok)
                && ok.ValueKind is JsonValueKind.True or JsonValueKind.False;
        }
        catch (JsonException) { return false; }
    }

    private static async Task<byte[]?> ReadFrameAsync(Stream stream, int limit, CancellationToken token = default)
    {
        var header = new byte[4];
        var read = await stream.ReadAsync(header, token);
        if (read == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(read), token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 || length > limit) throw new IOException("Frame size denied.");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, token);
        return body;
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] body, CancellationToken token = default)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(body, token);
        await stream.FlushAsync(token);
    }

    private static bool ServerIsApp(NamedPipeClientStream pipe, string expected)
    {
        try
        {
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) || pid == 0) return false;
            using var process = Process.GetProcessById(checked((int)pid));
            return string.Equals(Path.GetFullPath(process.MainModule?.FileName ?? ""), Path.GetFullPath(expected),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
}
