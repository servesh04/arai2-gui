using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AriaGui.Infrastructure.Downloads;

// Root-first handles deny deletion/renaming of every ancestor while pathname
// operations run. OPEN_REPARSE_POINT rejects junctions rather than following them.
internal sealed class PublicationDirectories : IDisposable
{
    private readonly List<SafeFileHandle> _handles = [];
    private readonly Dictionary<string, string> _identities = new(StringComparer.OrdinalIgnoreCase);
    internal PublicationDirectories(params string[] directories) : this(false, directories) { }
    internal PublicationDirectories(bool denyWrites, params string[] directories)
    {
        try
        {
            foreach (var directory in directories)
            {
                var full = Path.GetFullPath(directory);
                var root = Path.GetPathRoot(full);
                if (root is null || root.Length != 3 || !char.IsAsciiLetter(root[0]) || root[1] != ':') throw new IOException("Network or device publication directories are unsupported.");
                var chain = new Stack<string>();
                for (var cursor = full; cursor is not null; cursor = Path.GetDirectoryName(cursor)) chain.Push(cursor);
                while (chain.TryPop(out var path))
                {
                    if (_identities.ContainsKey(path)) continue;
                    var handle = CreateFileW(IdentifiedFile.NativePath(path), 0x80000000, denyWrites ? 1u : 3u, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
                    if (handle.IsInvalid) { handle.Dispose(); throw Failure(); }
                    _handles.Add(handle);
                    if (!GetFileInformationByHandle(handle, out var info)) throw Failure();
                    if ((info.Attributes & 0x400) != 0 || (info.Attributes & 0x10) == 0) throw new IOException("Unsafe publication directory.");
                    _identities.Add(path, $"{info.Volume:x8}:{info.IndexHigh:x8}{info.IndexLow:x8}");
                }
            }
        }
        catch { Dispose(); throw; }
    }
    internal string Identity(string directory) => _identities[Path.GetFullPath(directory)];
    public void Dispose() { foreach (var handle in _handles.AsEnumerable().Reverse()) handle.Dispose(); _handles.Clear(); }
    private static IOException Failure() => new("Publication directory identity unavailable.", new Win32Exception(Marshal.GetLastWin32Error()));
    [StructLayout(LayoutKind.Sequential)] private struct Information
    {
        public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Information information);
}
