using AriaGui.Application.Engine;

namespace AriaGui.Application.Tests;

public sealed class SyntheticEngineFixturePortTests
{
    [Fact]
    public void Untrusted_launch_arguments_cannot_select_a_fixture_port()
    {
        Assert.Equal(GuiLaunchKind.Rejected, OpenProtocolActivation.ClassifyLaunch(["--rpc-port=58554"]));
        Assert.Equal(GuiLaunchKind.Rejected, OpenProtocolActivation.ClassifyLaunch(["--browser-wakeup", "--rpc-port=58554"]));
        Assert.False(OpenProtocolActivation.IsOpen("ariagui://open?rpc-port=58554"));
    }

    [Fact]
    public void Private_port_requires_synthetic_marker_and_missing_setting_preserves_default()
    {
        var profile = Path.Combine(Path.GetTempPath(), "aria-gui-port-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        try
        {
            Assert.Throws<InvalidOperationException>(() => SyntheticEngineFixturePort.Read(profile));
            File.WriteAllText(Path.Combine(profile, "synthetic-r1-fixture"), "synthetic");
            Assert.Equal(16800, SyntheticEngineFixturePort.Read(profile));
            File.WriteAllText(Path.Combine(profile, SyntheticEngineFixturePort.FileName), "54321");
            Assert.Equal(54321, SyntheticEngineFixturePort.Read(profile));
        }
        finally { Directory.Delete(profile, true); }
    }

    [Theory]
    [InlineData("16800")] [InlineData("49151")] [InlineData("65536")] [InlineData("+54321")]
    [InlineData("54321\n")] [InlineData(" 54321")] [InlineData("543210")]
    public void Non_private_or_non_exact_port_is_rejected(string value)
    {
        var profile = Path.Combine(Path.GetTempPath(), "aria-gui-port-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        try
        {
            File.WriteAllText(Path.Combine(profile, "synthetic-r1-fixture"), "synthetic");
            File.WriteAllText(Path.Combine(profile, SyntheticEngineFixturePort.FileName), value);
            Assert.Throws<InvalidOperationException>(() => SyntheticEngineFixturePort.Read(profile));
        }
        finally { Directory.Delete(profile, true); }
    }
}
