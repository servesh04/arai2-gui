using AriaGui.Core.Downloads;

namespace AriaGui.Core.Tests;

public sealed class DownloadTests
{
    [Theory]
    [InlineData(DownloadStatus.Queued, DownloadStatus.Downloading, true)]
    [InlineData(DownloadStatus.Queued, DownloadStatus.Completed, true)]
    [InlineData(DownloadStatus.Downloading, DownloadStatus.Paused, true)]
    [InlineData(DownloadStatus.Paused, DownloadStatus.Downloading, true)]
    [InlineData(DownloadStatus.Downloading, DownloadStatus.Completed, true)]
    [InlineData(DownloadStatus.Completed, DownloadStatus.Downloading, false)]
    [InlineData(DownloadStatus.Failed, DownloadStatus.Queued, false)]
    [InlineData(DownloadStatus.Removed, DownloadStatus.Downloading, false)]
    public void Observed_transitions_do_not_revive_terminal_transfers(DownloadStatus from, DownloadStatus to, bool allowed) =>
        Assert.Equal(allowed, DownloadStateTransitions.CanTransition(from, to));

    [Fact]
    public void Unknown_size_has_no_percentage_or_eta()
    {
        var progress = new DownloadSnapshot(DownloadStatus.Downloading, 100, null, 10);
        Assert.Null(progress.Percentage);
        Assert.Null(progress.EstimatedTimeRemaining);
    }

    [Fact]
    public void Known_progress_computes_percentage_and_eta()
    {
        var progress = new DownloadSnapshot(DownloadStatus.Downloading, 25, 100, 5);
        Assert.Equal(25d, progress.Percentage);
        Assert.Equal(TimeSpan.FromSeconds(15), progress.EstimatedTimeRemaining);
    }

    [Fact]
    public void Zero_speed_has_no_eta() =>
        Assert.Null(new DownloadSnapshot(DownloadStatus.Downloading, 25, 100, 0).EstimatedTimeRemaining);

    [Fact]
    public void Empty_completed_file_is_complete_without_dividing_by_zero()
    {
        var progress = new DownloadSnapshot(DownloadStatus.Completed, 0, 0, 100);
        Assert.Equal(100d, progress.Percentage);
        Assert.Equal(0, progress.BytesPerSecond);
        Assert.Null(progress.EstimatedTimeRemaining);
    }

    [Theory]
    [InlineData(-1, 10, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 10, -1)]
    [InlineData(11, 10, 1)]
    public void Invalid_measurements_are_rejected(long downloaded, long total, long rate) =>
        Assert.ThrowsAny<ArgumentException>(() => new DownloadSnapshot(DownloadStatus.Downloading, downloaded, total, rate));
}
