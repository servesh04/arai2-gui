using AriaGui.Browser.Contracts;

namespace AriaGui.Application.Engine;

public enum GuiLaunchKind { Rejected, Plain, BrowserWake, OpenUri }

// Lexical allowlist of the delivered value, before app-side normalization/decoding.
// Windows shell can append the root slash before delivery. No payload is retained.
public static class OpenProtocolActivation
{
    public const string Uri = "ariagui://open";
    public static bool IsOpen(string? value) => value is { Length: 14 or 15 } &&
        (string.Equals(value, Uri, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(value, Uri + "/", StringComparison.OrdinalIgnoreCase));
    public static bool IsPlainLaunch(IReadOnlyList<string> arguments) => arguments.Count == 0;
    public static bool IsOpenLaunch(IReadOnlyList<string> arguments) => arguments.Count == 1 && IsOpen(arguments[0]);
    public static GuiLaunchKind ClassifyLaunch(IReadOnlyList<string> arguments) =>
        IsPlainLaunch(arguments) ? GuiLaunchKind.Plain :
        GuiLaunchContract.IsBrowserWake(arguments) ? GuiLaunchKind.BrowserWake :
        IsOpenLaunch(arguments) ? GuiLaunchKind.OpenUri : GuiLaunchKind.Rejected;
}

// At most one queued UI callback and one payload-free startup latch. UI host remains the
// last authority for closing; this controller neither creates a window nor owns the engine.
public sealed class OpenActivationController(TimeProvider clock, Func<Action, bool> enqueue, Action show)
{
    private readonly object _gate = new();
    private bool _ready, _pending, _scheduled, _stopped;
    private long? _lastAdmission;
    private long _pendingSince;
    public bool RequestOpen()
    {
        bool schedule;
        lock (_gate)
        {
            if (_stopped) return false;
            if (_pending) return true; // Coalesce without extending the startup deadline.
            var now = clock.GetTimestamp();
            if (_lastAdmission is { } last && clock.GetElapsedTime(last, now) < TimeSpan.FromMilliseconds(500)) return false;
            _lastAdmission = now; _pendingSince = now; _pending = true;
            schedule = ScheduleLocked();
        }
        if (schedule) Submit();
        return true;
    }
    public void Ready()
    {
        bool schedule;
        lock (_gate) { if (_stopped) return; _ready = true; schedule = ScheduleLocked(); }
        if (schedule) Submit();
    }
    public void Stop() { lock (_gate) { _stopped = true; _pending = false; } }
    private bool ScheduleLocked()
    {
        if (!_ready || !_pending || _scheduled) return false;
        _scheduled = true; return true;
    }
    private void Submit()
    {
        if (enqueue(Drain)) return;
        lock (_gate) { _scheduled = false; _pending = false; }
    }
    private void Drain()
    {
        lock (_gate)
        {
            _scheduled = false;
            if (_stopped || !_pending) return;
            _pending = false;
            if (clock.GetElapsedTime(_pendingSince) > TimeSpan.FromSeconds(30)) return;
        }
        show();
    }
}
