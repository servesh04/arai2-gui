using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace AriaGui.Browser.Spikes;

internal static class AttachmentSpike
{
    internal static Task<object> ProbeAsync(string root)
    {
        var result = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            object? instance = null;
            try
            {
                var file = Path.Combine(root, "synthetic-attachment.txt");
                File.WriteAllText(file, "Harmless synthetic Package 0 attachment fixture.");
                var before = SHA256.HashData(File.ReadAllBytes(file));
                instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("4125DD96-E03A-4103-8F70-E0597D803B9C"), true)!);
                var attachment = (IAttachmentExecute)instance!;
                var id = new Guid("BEA7C4F3-FA2B-45F1-8925-F95D758C0A84");
                Marshal.ThrowExceptionForHR(attachment.SetClientGuid(ref id));
                Marshal.ThrowExceptionForHR(attachment.SetLocalPath(file));
                Marshal.ThrowExceptionForHR(attachment.SetFileName(Path.GetFileName(file)));
                // No real URL, query, referrer or credentials are used in this probe.
                Marshal.ThrowExceptionForHR(attachment.SetSource("https://example.invalid/synthetic-attachment.txt"));
                var policy = attachment.CheckPolicy();
                var saved = attachment.Save(); Marshal.ThrowExceptionForHR(saved);
                var zone = File.ReadAllText(file + ":Zone.Identifier");
                Program.Check(zone.Contains("ZoneId=3", StringComparison.Ordinal), "Internet-zone mark");
                Program.Check(before.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(file))), "Main stream intact");
                result.SetResult(new
                {
                    checks = 2,
                    checkPolicyHresult = policy,
                    saveHresult = saved,
                    zone = 3,
                    mainStreamUnchanged = true,
                    fileOpened = false,
                    caveat = "NTFS/current policy only; no malware/reputation equivalence or non-NTFS proof"
                });
            }
            catch (Exception ex) { result.SetException(ex); }
            finally { if (instance is not null) Marshal.FinalReleaseComObject(instance); }
        })
        { IsBackground = true, Name = "WP0 Attachment STA" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return result.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
    // Preserve exact IAttachmentExecute vtable order; unused methods are retained.
    [ComImport, Guid("73DB1241-1E85-4581-8E4F-A81E1D0F8C57"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAttachmentExecute
    {
        [PreserveSig] int SetClientTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        [PreserveSig] int SetClientGuid(ref Guid guid);
        [PreserveSig] int SetLocalPath([MarshalAs(UnmanagedType.LPWStr)] string path);
        [PreserveSig] int SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int SetSource([MarshalAs(UnmanagedType.LPWStr)] string source);
        [PreserveSig] int SetReferrer([MarshalAs(UnmanagedType.LPWStr)] string referrer);
        [PreserveSig] int CheckPolicy();
        [PreserveSig] int Prompt(IntPtr parent, int prompt, out int action);
        [PreserveSig] int Save();
        [PreserveSig] int Execute(IntPtr parent, [MarshalAs(UnmanagedType.LPWStr)] string verb, out IntPtr process);
        [PreserveSig] int SaveWithUI(IntPtr parent);
        [PreserveSig] int ClearClientState();
    }
}
