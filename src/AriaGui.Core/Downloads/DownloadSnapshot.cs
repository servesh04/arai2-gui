namespace AriaGui.Core.Downloads;

public sealed record DownloadSnapshot
{
    public DownloadStatus Status { get; }
    public long DownloadedBytes { get; }
    public long? TotalBytes { get; }
    public long BytesPerSecond { get; }

    public DownloadSnapshot(DownloadStatus status, long downloadedBytes, long? totalBytes, long bytesPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(downloadedBytes);
        if (totalBytes is < 0) throw new ArgumentOutOfRangeException(nameof(totalBytes));
        ArgumentOutOfRangeException.ThrowIfNegative(bytesPerSecond);
        if (totalBytes.HasValue && downloadedBytes > totalBytes.Value)
            throw new ArgumentException("Downloaded bytes cannot exceed the known total.");

        Status = status;
        DownloadedBytes = downloadedBytes;
        TotalBytes = totalBytes;
        BytesPerSecond = status == DownloadStatus.Downloading ? bytesPerSecond : 0;
    }

    public double? Percentage => TotalBytes switch
    {
        > 0 => 100d * DownloadedBytes / TotalBytes.Value,
        0 when Status == DownloadStatus.Completed => 100d,
        _ => null
    };

    public TimeSpan? EstimatedTimeRemaining =>
        Status == DownloadStatus.Downloading && TotalBytes.HasValue && BytesPerSecond > 0
            ? TimeSpan.FromSeconds(Math.Min(TimeSpan.MaxValue.TotalSeconds - 1,
                (TotalBytes.Value - DownloadedBytes) / (double)BytesPerSecond))
            : null;
}
