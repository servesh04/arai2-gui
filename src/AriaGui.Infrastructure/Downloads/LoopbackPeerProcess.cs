using System.Runtime.InteropServices;

namespace AriaGui.Infrastructure.Downloads;

internal static class LoopbackPeerProcess
{
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr buffer, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);
    public static bool IsExpected(int clientPort, int serverPort, int expectedPid)
    {
        if (!OperatingSystem.IsWindows() || expectedPid <= 0) return false;
        var size = 0; GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 5, 0);
        if (size is < 4 or > 2 * 1024 * 1024) return false;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, 2, 5, 0) != 0) return false;
            var count = Marshal.ReadInt32(buffer);
            if (count < 0 || count > (size - 4) / 24) return false;
            for (var index = 0; index < count; index++)
            {
                var row = IntPtr.Add(buffer, 4 + index * 24);
                var local = Marshal.ReadInt32(row, 4); var remote = Marshal.ReadInt32(row, 12);
                var localPort = (ushort)System.Net.IPAddress.NetworkToHostOrder((short)Marshal.ReadInt32(row, 8));
                var remotePort = (ushort)System.Net.IPAddress.NetworkToHostOrder((short)Marshal.ReadInt32(row, 16));
                if (local == 0x0100007f && remote == 0x0100007f && localPort == clientPort && remotePort == serverPort
                    && Marshal.ReadInt32(row, 20) == expectedPid) return true;
            }
            return false;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
