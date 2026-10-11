using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace AriaGui.Browser.Spikes;

internal static class PipeSpike
{
    private const uint FirstInstance = 0x80000, Overlapped = 0x40000000, RejectRemote = 8;
    internal static string Endpoint(string channel, string installation, string packageModel, string runtime, string sid) =>
        "AriaGUI.wp0." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\n', channel, installation, packageModel, Path.GetFullPath(runtime).ToUpperInvariant(), sid))))[..32];
    private static SafePipeHandle Create(string name, string sddl)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var descriptor, out _)) throw new Win32Exception();
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var handle = CreateNamedPipe("\\\\.\\pipe\\" + name, 3 | FirstInstance | Overlapped,
                RejectRemote, 2, 65540, 65540, 0, ref attributes);
            if (handle.IsInvalid) { var code = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(code); }
            return handle;
        }
        finally { LocalFree(descriptor); }
    }
    internal static async Task ClientAsync(string name, string serverName)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new NamedPipeClientStream(serverName, name, PipeDirection.InOut, PipeOptions.Asynchronous);
        Console.Error.WriteLine("pipe_stage:connecting");
        await client.ConnectAsync(deadline.Token);
        Console.Error.WriteLine("pipe_stage:connected");
        Program.Check(GetNamedPipeServerProcessId(client.SafePipeHandle, out var serverId) && serverId > 0, "Server identity available");
        await NativeSpike.WriteAsync(client, "{\"hello\":true}"u8.ToArray(), deadline.Token);
        Console.Error.WriteLine("pipe_stage:request-written");
        Program.Check((await NativeSpike.ReadAsync(client, deadline.Token))!.AsSpan().SequenceEqual("{\"ready\":true}"u8), "Reply");
        Console.WriteLine(serverId);
        Console.Error.WriteLine("pipe_stage:complete");
    }
    internal static async Task<object> ProbeAsync(string root)
    {
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var install = Guid.NewGuid().ToString("D");
        var name = Endpoint("spike", install, "unpackaged", root, sid);
        // Protected ACL: explicit network denial; only this user can read/write/create an instance.
        var sddl = $"D:P(D;;GA;;;NU)(A;;GA;;;{sid})";
        using var handle = Create(name, sddl);
        var competingDenied = false;
        try { using var competitor = Create(name, sddl); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5) { competingDenied = true; }
        Program.Check(competingDenied, "First-instance squatting exclusion");
        using var server = new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var waiting = server.WaitForConnectionAsync(deadline.Token);
        using var child = NativeSpike.Start("pipe-client", name, "."); child.StandardInput.Close();
        var stage = "waiting-for-client";
        var watch = Stopwatch.StartNew();
        var errorOutput = child.StandardError.ReadToEndAsync();
        var childExit = child.WaitForExitAsync();
        try
        {
            if (await Task.WhenAny(waiting, childExit) == childExit && !waiting.IsCompleted)
                throw new InvalidOperationException("Pipe child exited before connecting.");
            await waiting;
            stage = "verifying-client";
            Program.Check(GetNamedPipeClientProcessId(server.SafePipeHandle, out var pid) && pid == child.Id, "Expected client process");
            stage = "reading-request";
            Program.Check((await NativeSpike.ReadAsync(server, deadline.Token))!.AsSpan().SequenceEqual("{\"hello\":true}"u8), "Request");
            stage = "writing-reply";
            await NativeSpike.WriteAsync(server, "{\"ready\":true}"u8.ToArray(), deadline.Token);
            stage = "waiting-for-child-exit";
            await child.WaitForExitAsync(deadline.Token);
            Program.Check(child.ExitCode == 0 && (await child.StandardOutput.ReadToEndAsync(deadline.Token)).Trim() == Environment.ProcessId.ToString(), "Expected server process");
            stage = "complete";
        }
        finally
        {
            deadline.Cancel();
            var killed = false;
            try
            {
                if (!child.HasExited) { child.Kill(entireProcessTree: true); killed = true; }
                await childExit.WaitAsync(TimeSpan.FromSeconds(3));
            }
            finally
            {
                // Only fixed diagnostic tokens from our own child; no endpoint, SID or payload logging.
                var stderr = errorOutput.IsCompletedSuccessfully ? errorOutput.Result : "";
                var tokens = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim())
                    .Where(line => line.StartsWith("pipe_stage:", StringComparison.Ordinal) || line.StartsWith("spike_error:", StringComparison.Ordinal)).ToArray();
                File.WriteAllText(Path.Combine(root, "pipe-diagnostics.json"), JsonSerializer.Serialize(new
                {
                    stage,
                    elapsedMilliseconds = watch.ElapsedMilliseconds,
                    childExited = child.HasExited,
                    childExitCode = child.HasExited ? child.ExitCode : (int?)null,
                    childKilled = killed,
                    childTokens = tokens,
                    parentDeadlineSeconds = 15,
                    childDeadlineSeconds = 5
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        var endpoints = new[] { name, Endpoint("spike", install, "packaged", root, sid), Endpoint("release", install, "unpackaged", root, sid),
            Endpoint("spike", Guid.NewGuid().ToString("D"), "unpackaged", root, sid), Endpoint("spike", install, "unpackaged", root + "-other", sid) };
        Program.Check(endpoints.Distinct().Count() == endpoints.Length, "Installation/model/channel/runtime isolation");
        return new
        {
            checks = 5,
            currentUserOnlyExplicitAcl = true,
            remoteRejectionFlag = RejectRemote,
            competingServerDenied = competingDenied,
            sameUserSeparateProcess = "passed",
            serverAndClientPidsVerified = true,
            crossUserActualProcess = "unverified: no alternate-user test token",
            remoteActualMachine = "unverified: no second machine",
            packagedActivation = "unverified: naming-only isolation proof"
        };
    }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { internal int Length; internal IntPtr Descriptor; internal int Inherit; }
    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances, uint outSize, uint inSize, uint timeout, ref SecurityAttributes attributes);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr address);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle handle, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle handle, out uint pid);
}
