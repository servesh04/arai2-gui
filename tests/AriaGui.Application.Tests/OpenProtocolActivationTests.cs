using AriaGui.Application.Engine;

namespace AriaGui.Application.Tests;

public sealed class OpenProtocolActivationTests
{
    [Fact]
    public void Installed_native_host_command_is_compatible_with_gui_classifier()
    {
        var arguments = new[] { AriaGui.Browser.Contracts.GuiLaunchContract.BrowserWakeArgument };
        Assert.True(AriaGui.Browser.Contracts.GuiLaunchContract.IsBrowserWake(arguments));
        Assert.Equal(GuiLaunchKind.BrowserWake, OpenProtocolActivation.ClassifyLaunch(arguments));
        Assert.Equal(GuiLaunchKind.Plain, OpenProtocolActivation.ClassifyLaunch([]));
        Assert.Equal(GuiLaunchKind.OpenUri, OpenProtocolActivation.ClassifyLaunch(["ARIAGUI://OPEN"]));
    }

    [Theory]
    [InlineData("--Browser-Wakeup")] [InlineData("--browser-wakeup ")]
    [InlineData("--bridge-auto-host")] [InlineData("--browser-wakeup=secret")]
    public void Internal_wake_variants_are_rejected(string argument) =>
        Assert.Equal(GuiLaunchKind.Rejected, OpenProtocolActivation.ClassifyLaunch([argument]));

    [Fact]
    public void Internal_wake_never_admits_extra_arguments() =>
        Assert.Equal(GuiLaunchKind.Rejected, OpenProtocolActivation.ClassifyLaunch(["--browser-wakeup", "ariagui://open"]));

    [Theory]
    [InlineData("ariagui://open")]
    [InlineData("ARIAGUI://OPEN")]
    [InlineData("ariagui://open/")]
    [InlineData("ARIAGUI://OPEN/")]
    public void Only_exact_open_is_admitted(string value) => Assert.True(OpenProtocolActivation.IsOpen(value));

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("ariagui:open")] [InlineData("ariagui://open//")]
    [InlineData("ariagui://open?url=https://example.invalid")] [InlineData("ariagui://open#secret")]
    [InlineData("ariagui://user@open")] [InlineData("ariagui://open:80")] [InlineData("ariagui://open/file")]
    [InlineData("ariagui://%6fpen")] [InlineData("ariagui://open%00")] [InlineData("ariagui://open\r\n")]
    [InlineData(" ariagui://open")] [InlineData("ariagui://open ")] [InlineData("\"ariagui://open\"")]
    [InlineData("https://open")] [InlineData("ariagui://add")] [InlineData("ariagui://open\\evil")]
    public void Unsupported_payload_never_normalizes_into_open(string? value) => Assert.False(OpenProtocolActivation.IsOpen(value));

    [Fact]
    public void Launch_argument_count_and_long_payload_are_bounded()
    {
        Assert.True(OpenProtocolActivation.IsPlainLaunch([]));
        Assert.True(OpenProtocolActivation.IsOpenLaunch([OpenProtocolActivation.Uri]));
        Assert.False(OpenProtocolActivation.IsOpenLaunch([]));
        Assert.False(OpenProtocolActivation.IsOpenLaunch([OpenProtocolActivation.Uri, "--arbitrary"]));
        Assert.False(OpenProtocolActivation.IsPlainLaunch(["--arbitrary"]));
        Assert.False(OpenProtocolActivation.IsOpen(new string('x', 100_000)));
    }

    [Fact]
    public void Cold_start_coalesces_until_ready_then_warm_tray_open_reuses_host()
    {
        var clock = new Clock(); var queue = new Queue<Action>(); var host = new Host(); var desktop = new DesktopLifetimeController(host);
        var activation = new OpenActivationController(clock, action => {queue.Enqueue(action); return true;}, desktop.Open);
        for (var i = 0; i < 100; i++) Assert.True(activation.RequestOpen());
        Assert.Empty(queue); Assert.Equal(0, host.Shows);
        activation.Ready(); Assert.Single(queue); queue.Dequeue()(); Assert.Equal(1, host.Shows);
        Assert.True(desktop.CloseToTray()); Assert.True(host.Hidden);
        Assert.False(activation.RequestOpen()); // A burst cannot cause repeated focus changes.
        clock.Advance(TimeSpan.FromSeconds(1)); Assert.True(activation.RequestOpen());
        queue.Dequeue()(); Assert.False(host.Hidden); Assert.Equal(2, host.Shows);
    }

    [Fact]
    public async Task Closing_rejects_new_opens_and_discards_already_queued_activation()
    {
        var queue = new Queue<Action>(); var host = new Host(); var desktop = new DesktopLifetimeController(host);
        var activation = new OpenActivationController(new Clock(), action => {queue.Enqueue(action); return true;}, desktop.Open);
        activation.Ready(); activation.RequestOpen(); activation.Stop();
        await desktop.ExitAsync(); queue.Dequeue()();
        Assert.False(activation.RequestOpen()); Assert.Equal(0, host.Shows); Assert.Equal(1, host.Exits);
        desktop.Open(); Assert.Equal(0, host.Shows);
    }

    [Fact]
    public void Expired_startup_latch_and_failed_dispatch_are_not_replayed()
    {
        var clock = new Clock(); var queue = new Queue<Action>(); var shows = 0;
        var activation = new OpenActivationController(clock, action => {queue.Enqueue(action); return true;}, () => shows++);
        activation.RequestOpen(); clock.Advance(TimeSpan.FromSeconds(31)); activation.Ready(); queue.Dequeue()(); Assert.Equal(0, shows);
        activation.RequestOpen(); queue.Dequeue()(); Assert.Equal(1, shows);
        var failed = new OpenActivationController(clock, _ => false, () => shows++);
        failed.Ready(); failed.RequestOpen(); clock.Advance(TimeSpan.FromSeconds(1)); failed.RequestOpen(); Assert.Equal(1, shows);
    }

    [Fact]
    public void Concurrent_burst_queues_one_callback_without_payload_storage()
    {
        var queued = new List<Action>(); var shows = 0;
        var activation = new OpenActivationController(new Clock(), action => { lock (queued) queued.Add(action); return true;}, () => shows++);
        activation.Ready(); Parallel.For(0, 1000, _ => activation.RequestOpen());
        Assert.Single(queued); queued[0](); Assert.Equal(1, shows);
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        internal void Advance(TimeSpan time) => _ticks += time.Ticks;
    }
    private sealed class Host : IDesktopLifetimeHost
    {
        internal int Shows, Exits; internal bool Hidden;
        public bool EnsureTray() => true;
        public void Hide() => Hidden = true;
        public void Show() {Shows++; Hidden = false;}
        public Task ExitAsync() {Exits++; return Task.CompletedTask;}
    }
}
