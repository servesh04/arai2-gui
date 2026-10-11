using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace AriaGui.Infrastructure.Browser;

internal static class PrivateBrowserPipe
{
    internal static NamedPipeServerStream Create(BrowserBridgeEndpoint endpoint, bool first)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor($"D:P(D;;GA;;;NU)(A;;GA;;;{endpoint.UserSid})", 1, out var descriptor, out _))
            throw new Win32Exception();
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var handle = CreateNamedPipe("\\\\.\\pipe\\" + endpoint.PipeName, 3 | 0x40000000u | (first ? 0x80000u : 0),
                8, BrowserBridgeServer.MaximumSessions, 65540, 65540, 0, ref attributes);
            if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
            return new(PipeDirection.InOut, true, false, handle);
        }
        finally { LocalFree(descriptor); }
    }
    internal static bool VerifyPeer(NamedPipeServerStream stream, string sid, string executable)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (!GetNamedPipeClientProcessId(stream.SafePipeHandle, out var pid) || pid == 0) return false;
        var sameUser = false;
        try
        {
            stream.RunAsClient(() => { if (!OperatingSystem.IsWindows()) return; using var identity = WindowsIdentity.GetCurrent(true); sameUser = identity?.User?.Value == sid; });
            if (!sameUser) return false;
            using var process = Process.GetProcessById(checked((int)pid));
            var path = process.MainModule?.FileName;
            return path is not null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException or UnauthorizedAccessException) { return false; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { internal int Length; internal IntPtr Descriptor; internal int Inherit; }
    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint mode, uint max, uint output, uint input, uint timeout, ref SecurityAttributes attributes);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr address);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle handle, out uint pid);
}
