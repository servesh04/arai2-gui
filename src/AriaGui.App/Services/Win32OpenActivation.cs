using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AriaGui.Application.Engine;

namespace AriaGui.App.Services;

// Payload-free wake signal independent of tray icon availability. The existing lease
// still selects the owner; runtime scoping prevents opening another verification/channel.
internal sealed class Win32OpenActivation : IDisposable
{
    private readonly nint _window;
    private readonly uint _message;
    private readonly string _scope;
    private readonly SubclassProc _callback;
    private readonly OpenActivationController _controller;
    private bool _disposed;
    internal Win32OpenActivation(nint window, string runtime, OpenActivationController controller)
    {
        _window = window; _controller = controller; _scope = MessageName(runtime); _message = RegisterWindowMessage(_scope); _callback = WindowProc;
        if (_message == 0 || !SetWindowSubclass(window, _callback, 2, 0)) throw new InvalidOperationException("Open activation is unavailable.");
        if (!SetProp(window, _scope, 1)) { RemoveWindowSubclass(window, _callback, 2); throw new InvalidOperationException("Open activation publication is unavailable."); }
    }
    internal static void RequestExistingWindow(string runtime)
    {
        var scope = MessageName(runtime);
        var message = RegisterWindowMessage(scope);
        if (message == 0) return;
        nint target = 0;
        EnumWindows((window, _) => { if (GetProp(window, scope) != 1) return true; target = window; return false; }, 0);
        if (target != 0)
        {
            GetWindowThreadProcessId(target, out var primaryProcess);
            // Transfer only this launcher process's foreground permission to the scoped
            // matching scoped window. Publication is routing, not authenticated lease proof.
            // Windows may refuse it; showing/restoring remains best effort.
            if (primaryProcess != 0) AllowSetForegroundWindow(primaryProcess);
            PostMessage(target, message, 0, 0);
        }
        else PostMessage(0xffff, message, 0, 0);
        // No ACK means no acceptance claim. A primary still starting activates its own window;
        // closing owners ignore this request and retain their lease through shutdown.
    }
    private static string MessageName(string runtime)
    {
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        var scope = sid + "|" + Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtime)).ToUpperInvariant();
        return "AriaGUI.Open.v2." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
    }
    private nint WindowProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint reference)
    {
        try { if (!_disposed && message == _message && wParam == 0 && lParam == 0) _controller.RequestOpen(); }
        catch { /* Never cross the native callback with a managed exception. */ }
        return DefSubclassProc(window, message, wParam, lParam);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _controller.Stop(); RemoveProp(_window, _scope); RemoveWindowSubclass(_window, _callback, 2); GC.SuppressFinalize(this);
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint reference);
    private delegate bool EnumProc(nint window, nint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(nint window, SubclassProc callback, nuint id, nuint reference);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(nint window, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumProc callback, nint data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("user32.dll", EntryPoint = "SetPropW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetProp(nint window, string name, nint value);
    [DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = CharSet.Unicode)] private static extern nint GetProp(nint window, string name);
    [DllImport("user32.dll", EntryPoint = "RemovePropW", CharSet = CharSet.Unicode)] private static extern nint RemoveProp(nint window, string name);
}
