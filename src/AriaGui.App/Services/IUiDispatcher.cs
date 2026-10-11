namespace AriaGui.App.Services;

public interface IUiDispatcher
{
    void Enqueue(Action action);
}
