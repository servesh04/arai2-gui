using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AriaGui.Infrastructure.Downloads;

// Hash and rename the same Windows file object while denying other writers/deleters.
// Publication never replaces an occupied name; retained objects stay in private backup.
internal sealed class IdentifiedFile : IDisposable
{
    private readonly FileStream _stream;
    internal IdentifiedFile(string path, bool flushForPublication = false)
    {
        if (!OperatingSystem.IsWindows()) throw new IOException("Identified publication requires Windows.");
        var handle = CreateFileW(NativePath(path), 0x80000000 | 0x00010000 | (flushForPublication ? 0x40000000u : 0u), 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw Failure(); }
        try
        {
            if (!GetFileInformationByHandleEx(handle, 9, out var attributes, 8)) throw Failure();
            if ((attributes.Attributes & 0x400) != 0) throw new IOException("Reparse publication object denied.");
            _stream = new FileStream(handle, flushForPublication ? FileAccess.ReadWrite : FileAccess.Read, 4096, false);
            if (flushForPublication) _stream.Flush(true);
        }
        catch { handle.Dispose(); throw; }
    }
    internal async Task<string> HashAsync(CancellationToken token)
    { _stream.Position = 0; return Convert.ToHexString(await SHA256.HashDataAsync(_stream, token)); }
    internal void FlushToDisk() => _stream.Flush(true);
    internal void RenameWithoutReplacement(string destination)
    {
        var name = Encoding.Unicode.GetBytes(NativePath(destination));
        var rootOffset = IntPtr.Size == 8 ? 8 : 4;
        var lengthOffset = rootOffset + IntPtr.Size; var nameOffset = lengthOffset + 4;
        var data = new byte[nameOffset + name.Length + 2]; // NUL-terminated; length excludes the terminator.
        BitConverter.GetBytes(name.Length).CopyTo(data, lengthOffset); name.CopyTo(data, nameOffset);
        var buffer = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, buffer, data.Length);
            if (!SetFileInformationByHandle(_stream.SafeFileHandle, 3, buffer, (uint)data.Length)) throw Failure();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static IOException Failure() => new("Identified file operation failed.", new Win32Exception(Marshal.GetLastWin32Error()));
    // Canonical local paths have already passed the protected destination policy.
    // The prefix is only for native calls; persisted paths remain canonical DOS
    // paths, and caller-supplied device/UNC aliases are not accepted here.
    internal static string NativePath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.Length < 3 || !char.IsAsciiLetter(full[0]) || full[1] != ':' || full[2] != '\\')
            throw new IOException("Only canonical local paths are supported.");
        return @"\\?\" + full;
    }
    public void Dispose() => _stream.Dispose();
    [StructLayout(LayoutKind.Sequential)] private struct AttributeTag { public uint Attributes; public uint Tag; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int info, out AttributeTag value, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int info, IntPtr value, uint size);
}
