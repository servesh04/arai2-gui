using AriaGui.Application.Downloads;

namespace AriaGui.App.Features;

public sealed class BrowserHandoffNotifier : IHandoffNotifier, IDisposable
{
    private readonly BrowserHandoffService _service;
    public BrowserHandoffNotifier(BrowserHandoffService service)
    {
        _service = service;
        _service.Added += OnAdded;
    }
    public event EventHandler<HandoffAddedEventArgs>? AddedFromBrowser;
    private void OnAdded(object? sender, BrowserHandoffSubmission item)
        => AddedFromBrowser?.Invoke(this, new(item.Id, item.DisplayName, item.AddedPaused));
    public Task<bool> UndoAsync(Guid itemId, CancellationToken cancellationToken = default)
        => _service.UndoAsync(itemId, cancellationToken);
    public void Dispose() => _service.Added -= OnAdded;
}
