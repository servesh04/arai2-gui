using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AriaGui.Application.Engine;

namespace AriaGui.Infrastructure.Aria2;

// Explicit bInheritHandles=false prevents GUI locks/stdio pipes from surviving in aria2.
// Use CreateNoWindow, inherited environment, structured quoting, and an explicit executable.
internal static class NonInheritingProcessLauncher
{
    public static Process Start(string executable, IReadOnlyList<string> arguments, string directory)
    {
        var command = new StringBuilder(string.Join(" ", new[] { executable }.Concat(arguments).Select(Quote)));
        var startup = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfo>() };
        if (!CreateProcess(executable, command, 0, 0, false, 0x08000000, 0, directory, ref startup, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try { return Process.GetProcessById((int)info.ProcessId); }
        catch (ArgumentException)
        { throw new EngineException(EngineFailure.ProcessExited, "aria2 stopped before it became ready. Check the engine diagnostic log."); }
        finally { CloseHandle(info.Thread); CloseHandle(info.Process); }
    }
    private static string Quote(string value)
    {
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint Size; public nint Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedBytes;
        public nint ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string application, StringBuilder commandLine, nint processSecurity, nint threadSecurity,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment, string directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
}
