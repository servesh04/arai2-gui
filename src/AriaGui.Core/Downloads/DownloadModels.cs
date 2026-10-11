namespace AriaGui.Core.Downloads;

// These immutable records describe the product, without WinUI, RPC, or database types.
public sealed record DownloadJob(Guid Id, string DisplayName, Uri Source, string Destination, DateTimeOffset CreatedAt);

public sealed record TransferAttempt(Guid Id, Guid JobId, string EngineId, DateTimeOffset StartedAt,
    Guid? ParentAttemptId = null);

public sealed record DownloadFile(Guid Id, Guid JobId, Guid AttemptId, string FileName, long? TotalBytes);

public enum DownloadAction { Add, Pause, Resume, Remove, Retry, Move, Recover }
public enum OperationStatus { Pending, Confirmed, Uncertain, Failed }

// The repository persists intent before the coordinator sends an engine mutation.
public sealed record PendingOperation(Guid Id, Guid JobId, DownloadAction Action,
    OperationStatus Status, DateTimeOffset RequestedAt, int? QueuePosition = null);
