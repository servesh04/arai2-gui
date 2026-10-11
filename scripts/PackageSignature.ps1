# Native trust result avoids relying on localized PowerShell StatusMessage text.
function Get-WindowsPackageTrust([string]$Path) {
    if (!('AriaGuiPackaging.TrustVerifier' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace AriaGuiPackaging {
    public static class TrustVerifier {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FileInfo {
            public uint Size;
            [MarshalAs(UnmanagedType.LPWStr)] public string Path;
            public IntPtr File;
            public IntPtr KnownSubject;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct TrustData {
            public uint Size;
            public IntPtr PolicyCallback, SipClient;
            public uint UiChoice, RevocationChecks, UnionChoice;
            public IntPtr FileInfo;
            public uint StateAction;
            public IntPtr StateData, UrlReference;
            public uint ProviderFlags, UiContext;
        }
        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
        public static uint Verify(string path) {
            var info = new FileInfo { Size = (uint)Marshal.SizeOf<FileInfo>(), Path = path };
            var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>());
            Marshal.StructureToPtr(info, pointer, false);
            var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2,
                UnionChoice = 1, FileInfo = pointer, StateAction = 1, ProviderFlags = 0x1000 };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            try { return unchecked((uint)WinVerifyTrust(new IntPtr(-1), ref action, ref data)); }
            finally {
                data.StateAction = 2;
                WinVerifyTrust(new IntPtr(-1), ref action, ref data);
                Marshal.DestroyStructure<FileInfo>(pointer);
                Marshal.FreeHGlobal(pointer);
            }
        }
    }
}
'@
    }
    [AriaGuiPackaging.TrustVerifier]::Verify($Path).ToString('X8')
}
