namespace AriaGui.Application.Engine;

public enum ObservableEngineState { Ready, Reconnecting, Stopped }

public static class EngineControlStateMachine
{
    public static ObservableEngineState Derive(EngineState state) => state.Status switch
    {
        EngineStatus.Ready => ObservableEngineState.Ready,
        EngineStatus.Starting => ObservableEngineState.Reconnecting,
        EngineStatus.Failed when state.Failure is EngineFailure.RpcUnavailable or EngineFailure.StartupTimeout
            && !state.Message.Contains("Automatic recovery stopped", StringComparison.Ordinal) => ObservableEngineState.Reconnecting,
        _ => ObservableEngineState.Stopped
    };
}
