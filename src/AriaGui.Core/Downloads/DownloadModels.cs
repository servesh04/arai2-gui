namespace AriaGui.Core.Downloads;

// These immutable records describe the product, without WinUI, RPC, or database types.
public sealed record ExpectedChecksum(string Sha256, ChecksumProvenance Provenance)
{
    public static ExpectedChecksum FromManualInput(string value)
    {
        if (value is null || value.Length != 64 || !value.All(Uri.IsHexDigit))
            throw new ArgumentException("Enter exactly 64 hexadecimal SHA-256 characters.", nameof(value));
        return new(value.ToUpperInvariant(), ChecksumProvenance.UserSupplied);
    }
    public void Validate()
    {
        if (Sha256 is not { Length: 64 } || !Sha256.All(Uri.IsHexDigit) || Provenance != ChecksumProvenance.UserSupplied)
            throw new ArgumentException("Invalid expected SHA-256 or provenance.");
    }
}
public enum ChecksumProvenance { UserSupplied = 1 }
public enum VerificationStatus { NotSupplied, Pending, Verifying, Verified, Mismatch }

public sealed record DownloadJob(Guid Id, string DisplayName, Uri Source, string Destination, DateTimeOffset CreatedAt,
    ExpectedChecksum? ExpectedChecksum = null);

public sealed record TransferAttempt(Guid Id, Guid JobId, string EngineId, DateTimeOffset StartedAt,
    Guid? ParentAttemptId = null, int HttpConnections = 1);

public sealed record DownloadFile(Guid Id, Guid JobId, Guid AttemptId, string FileName, long? TotalBytes);

public enum DownloadAction { Add, Pause, Resume, Remove, Retry, Move, Recover }
public enum OperationStatus { Pending, Confirmed, Uncertain, Failed }

// The repository persists intent before the coordinator sends an engine mutation.
public sealed record PendingOperation(Guid Id, Guid JobId, DownloadAction Action,
    OperationStatus Status, DateTimeOffset RequestedAt, int? QueuePosition = null);
