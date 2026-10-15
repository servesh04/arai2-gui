using AriaGui.Core.Downloads;

namespace AriaGui.Application.Downloads;

/// <summary>Null means unlimited; zero is never a rate limit or a pause command.</summary>
public readonly record struct ByteRateLimit(long? BytesPerSecond)
{
    public static ByteRateLimit Unlimited => new(null);

    public void Validate()
    {
        if (BytesPerSecond is < 1024 or > 1_073_741_824)
            throw new ArgumentOutOfRangeException(nameof(BytesPerSecond), "Choose unlimited or 1 KiB/s through 1 GiB/s.");
    }
}

public sealed record BandwidthProfile(string Name, ByteRateLimit Download, ByteRateLimit Upload)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 64 || Name.Any(char.IsControl))
            throw new ArgumentException("Choose a profile name up to 64 printable characters.", nameof(Name));
        Download.Validate(); Upload.Validate();
    }

    public static BandwidthProfile Unlimited { get; } = new("Unlimited", ByteRateLimit.Unlimited, ByteRateLimit.Unlimited);
}

public sealed record BandwidthPolicy(long RequestedRevision, long ConfirmedRevision, BandwidthProfile Requested,
    BandwidthProfile Confirmed, string? ConfirmedGeneration = null, OperationStatus? RequestStatus = null)
{
    public static BandwidthPolicy Default { get; } = new(0, 0, BandwidthProfile.Unlimited, BandwidthProfile.Unlimited);

    public void Validate()
    {
        if (RequestedRevision < 0 || ConfirmedRevision < 0 || ConfirmedRevision > RequestedRevision)
            throw new ArgumentOutOfRangeException(nameof(RequestedRevision));
        Requested.Validate(); Confirmed.Validate();
    }
}

public sealed record BandwidthObservation(string Generation, ByteRateLimit Download, ByteRateLimit Upload);

public sealed record JobBandwidthPolicy(Guid JobId, long RequestedRevision, long ConfirmedRevision,
    ByteRateLimit Requested, ByteRateLimit Confirmed, string? ConfirmedGeneration = null,
    OperationStatus? RequestStatus = null)
{
    public static JobBandwidthPolicy Default(Guid jobId) => new(jobId, 0, 0, ByteRateLimit.Unlimited, ByteRateLimit.Unlimited);

    public void Validate()
    {
        if (JobId == Guid.Empty || RequestedRevision < 0 || ConfirmedRevision < 0 || ConfirmedRevision > RequestedRevision)
            throw new ArgumentOutOfRangeException(nameof(JobId));
        Requested.Validate(); Confirmed.Validate();
    }
}

public sealed record JobBandwidthObservation(string Generation, string EngineId, ByteRateLimit Download);
