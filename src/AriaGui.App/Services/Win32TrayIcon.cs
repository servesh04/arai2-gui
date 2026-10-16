using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace AriaGui.App.Services;

// No WinForms host or extra message loop: subclass the existing WinUI HWND.
public sealed class Win32TrayIcon : IDisposable
{
    private const uint Callback = 0x8001;
    private readonly nint _window;
    private readonly DispatcherQueue _dispatcher;
    private readonly SubclassProc _callback;
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly nint _icon;
    private bool _added, _disposed;
    public event Action? OpenRequested, ExitRequested, Unavailable, Suspending, Resuming, ClockChanged;

    public Win32TrayIcon(nint window, DispatcherQueue dispatcher, string iconPath)
    {
        _window = window; _dispatcher = dispatcher; _callback = WindowProc;
        _icon = LoadImage(0, iconPath, 1, 16, 16, 0x10);
        if (_icon == 0) throw new InvalidOperationException("The tray icon could not be loaded.");
        if (!SetWindowSubclass(window, _callback, 1, 0)) { DestroyIcon(_icon); throw new InvalidOperationException("The tray callback could not be installed."); }
    }
    public bool EnsureRegistered()
    {
        if (_disposed) return false;
        var data = Data();
        if (_added && ShellNotifyIcon(1, ref data)) return true; // Explorer still owns this icon.
        _added = ShellNotifyIcon(0, ref data);
        if (_added) { data.Version = 4; ShellNotifyIcon(4, ref data); }
        return _added;
    }
    private NotifyIconData Data() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = _window, Id = 1, Flags = 1 | 2 | 4 | 0x80,
        Callback = Callback, Icon = _icon, Tip = "AriaGUI — Open / Exit", Info = "", InfoTitle = ""
    };
    private nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint reference)
    {
        try
        {
            if (message == _taskbarCreated)
            {
                _added = false;
                if (!EnsureRegistered()) Queue(() => Unavailable?.Invoke());
            }
            else if (message == 0x218)
            {
                if (wParam == 4) Queue(() => Suspending?.Invoke());
                else if (wParam is 7 or 18) Queue(() => Resuming?.Invoke());
            }
            else if (message is 0x001e or 0x001a) // WM_TIMECHANGE / WM_SETTINGCHANGE (including timezone).
                Queue(() => ClockChanged?.Invoke());
            else if (message == Callback)
            {
                var notification = (uint)((long)lParam & 0xffff);
                if (notification is 0x400 or 0x401 or 0x202 or 0x203) Queue(() => OpenRequested?.Invoke());
                else if (notification is 0x7b or 0x205) ShowMenu();
            }
        }
        catch { Queue(() => Unavailable?.Invoke()); } // Never let a managed exception cross a native callback.
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }
    private void Queue(Action action) => _dispatcher.TryEnqueue(action.Invoke);
    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == 0) { Queue(() => OpenRequested?.Invoke()); return; }
        try
        {
            AppendMenu(menu, 0, 1, "Open AriaGUI"); AppendMenu(menu, 0, 2, "Exit");
            GetCursorPos(out var point); SetForegroundWindow(_window);
            var choice = TrackPopupMenu(menu, 0x100 | 0x80, point.X, point.Y, 0, _window, 0);
            PostMessage(_window, 0, 0, 0);
            if (choice == 1) Queue(() => OpenRequested?.Invoke());
            else if (choice == 2) Queue(() => ExitRequested?.Invoke());
        }
        finally { DestroyMenu(menu); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var data = Data(); ShellNotifyIcon(2, ref data);
        RemoveWindowSubclass(_window, _callback, 1); DestroyIcon(_icon);
        GC.SuppressFinalize(this);
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint reference);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size; public nint Window; public uint Id, Flags, Callback; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint operation, ref NotifyIconData data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(nint window, SubclassProc callback, nuint id, nuint reference);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(nint window, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode)] private static extern nint LoadImage(nint instance, string path, uint type, int x, int y, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
}
