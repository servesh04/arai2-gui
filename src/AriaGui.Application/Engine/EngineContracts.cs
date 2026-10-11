namespace AriaGui.Application.Engine;

public enum EngineStatus { Stopped, Starting, Ready, Failed, Stopping }
public enum EngineFailure { MissingBinary, InvalidBinary, PortInUse, AlreadyRunning, StartupTimeout,
    ProcessExited, Authentication, RpcUnavailable, InvalidResponse, StorageUnavailable, TransferNotFound, RequestRejected }

public sealed record EngineInfo(string Version, string SessionId, string? RecoveryMessage = null);
public sealed record EngineState(long Revision, EngineStatus Status, string Message, string? Version = null,
    EngineFailure? Failure = null);

// Messages are deliberately safe for the screen; wire errors and credentials stay in Infrastructure.
public sealed class EngineException(EngineFailure failure, string message) : Exception(message)
{
    public EngineFailure Failure { get; } = failure;
}

public interface IManagedAria2Engine
{
    event EventHandler? UnexpectedExit;
    Task<EngineInfo> StartAsync(CancellationToken cancellationToken);
    Task<EngineInfo> CheckHealthAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    Task CheckpointAsync(CancellationToken token) => Task.CompletedTask;
}
