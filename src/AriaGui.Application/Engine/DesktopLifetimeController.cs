namespace AriaGui.Application.Engine;

public interface IDesktopLifetimeHost
{
    bool EnsureTray();
    void Hide();
    void Show();
    Task ExitAsync();
}

// Close and Exit are distinct actions. A failed tray never strands an invisible app.
public sealed class DesktopLifetimeController(IDesktopLifetimeHost host)
{
    private Task? _exit;
    private readonly object _gate = new();
    public bool IsExiting { get { lock (_gate) return _exit is not null; } }
    public bool CloseToTray()
    {
        lock (_gate)
        {
            if (_exit is not null) return false;
            if (!host.EnsureTray()) { host.Show(); return false; }
            host.Hide(); return true;
        }
    }
    public void Open() { lock (_gate) { if (_exit is null) host.Show(); } }
    public Task ExitAsync() { lock (_gate) return _exit ??= host.ExitAsync(); }
}
