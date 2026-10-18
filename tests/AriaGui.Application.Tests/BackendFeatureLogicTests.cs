using AriaGui.Application.Downloads;
using AriaGui.Application.Engine;

namespace AriaGui.Application.Tests;

public sealed class DownloadFailureMapperTests
{
    [Theory]
    [InlineData("2", null, "Download timed out", null)]
    [InlineData("3", null, "File not found", null)]
    [InlineData("9", null, "Disk full or destination write error", null)]
    [InlineData("31", null, "Checksum mismatch", null)]
    [InlineData("22", "The response status is not successful. status=404", "HTTP 404 - file not found", 404)]
    [InlineData("22", "HTTP/1.1 403 link expired", "HTTP 403 - link expired", 403)]
    [InlineData("77", "https://secret.example/a?cookie=x", "Download failed (code 77)", null)]
    public void MapsSafeUserFacingReason(string code, string? message, string reason, int? httpStatus)
    {
        var result = DownloadFailureMapper.Map(code, message);
        Assert.Equal(reason, result.Reason);
        Assert.Equal(httpStatus, result.HttpStatus);
        Assert.DoesNotContain("http://", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", result.Reason, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class EngineControlStateMachineTests
{
    [Theory]
    [InlineData(EngineStatus.Ready, null, "Ready", ObservableEngineState.Ready)]
    [InlineData(EngineStatus.Starting, null, "Starting", ObservableEngineState.Reconnecting)]
    [InlineData(EngineStatus.Failed, EngineFailure.RpcUnavailable, "Retrying", ObservableEngineState.Reconnecting)]
    [InlineData(EngineStatus.Failed, EngineFailure.RpcUnavailable, "Automatic recovery stopped after three attempts", ObservableEngineState.Stopped)]
    [InlineData(EngineStatus.Failed, EngineFailure.ProcessExited, "Exited", ObservableEngineState.Stopped)]
    public void DerivesObservableState(EngineStatus status, EngineFailure? failure, string message, ObservableEngineState expected) =>
        Assert.Equal(expected, EngineControlStateMachine.Derive(new(1, status, message, Failure: failure)));
}

public sealed class SpeedLimitPresetPolicyTests
{
    [Fact]
    public void ConvertsPresetsToAriaOptionValues()
    {
        var presets = SpeedLimitPresetPolicy.Build(new("Reduced", 8_000_000, null));
        Assert.Equal("0", SpeedLimitPresetPolicy.ToAriaOptionValue(presets.Single(value => value.Name == "Standard").BytesPerSecond));
        Assert.Equal("4000000", SpeedLimitPresetPolicy.ToAriaOptionValue(presets.Single(value => value.Name == "Reduced").BytesPerSecond));
        Assert.Equal("8000000", SpeedLimitPresetPolicy.ToAriaOptionValue(presets.Single(value => value.Name == "Custom").BytesPerSecond));
    }
}
