using Microsoft.UI.Dispatching;

namespace AriaGui.App.Services;

public sealed class WinUiDispatcher(DispatcherQueue queue) : IUiDispatcher
{
    public void Enqueue(Action action)
    {
        if (queue.HasThreadAccess) action();
        else queue.TryEnqueue(() => action()); // A closing window can reject updates.
    }
}
