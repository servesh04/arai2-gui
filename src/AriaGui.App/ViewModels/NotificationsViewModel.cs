using System.Collections.ObjectModel;
using AriaGui.Application.Notifications;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AriaGui.App.ViewModels;

public sealed class NotificationRowViewModel(NotificationEntry entry)
{
    public long Id => entry.Id;
    public Guid? JobId => entry.JobId;
    public string Title => entry.Kind switch
    {
        NotificationKind.TransferCompleted => "Transfer completed",
        NotificationKind.TransferFailed => "Transfer failed",
        _ => "Download engine needs attention"
    };
    public string DisplayName => entry.DisplayName;
    public string Detail => entry.Detail;
    public string TimeText => entry.CreatedAt.ToLocalTime().ToString("g");
    public string UnreadText => entry.ReadAt is null ? "Unread" : "";
    public bool CanViewDownload => entry.JobId is not null;
    public bool IsUnread => entry.ReadAt is null;
    public NotificationRowViewModel MarkedRead() => new(entry with { ReadAt = DateTimeOffset.UtcNow });
}

public sealed class NotificationsViewModel(INotificationRepository repository) : ObservableObject
{
    private long? _nextCursor;
    private bool _isBusy;
    private bool _hasMore;
    private int _unreadCount;
    private string _statusMessage = "";
    private string _emptyMessage = "";
    public ObservableCollection<NotificationRowViewModel> Items { get; } = [];
    public event Action<Guid>? ViewDownloadRequested;
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public bool HasMore { get => _hasMore; private set => SetProperty(ref _hasMore, value); }
    public bool HasItems => Items.Count > 0;
    public int UnreadCount { get => _unreadCount; private set => SetProperty(ref _unreadCount, value); }
    public string UnreadSummary => UnreadCount == 1 ? "1 unread" : $"{UnreadCount} unread";
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string EmptyMessage { get => _emptyMessage; private set => SetProperty(ref _emptyMessage, value); }

    public async Task RefreshAsync(CancellationToken token = default)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await repository.InitializeAsync(token);
            Items.Clear();
            _nextCursor = null;
            await LoadNextPageCoreAsync(token);
            StatusMessage = "";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { StatusMessage = "Notifications could not be loaded. Try again."; }
        finally { IsBusy = false; }
    }

    public async Task LoadMoreAsync(CancellationToken token = default)
    {
        if (IsBusy || !HasMore) return;
        IsBusy = true;
        try { await LoadNextPageCoreAsync(token); StatusMessage = ""; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { StatusMessage = "Older notifications could not be loaded. Try again."; }
        finally { IsBusy = false; }
    }

    public async Task ViewDownloadAsync(NotificationRowViewModel row, CancellationToken token = default)
    {
        if (row.JobId is not Guid jobId) return;
        await MarkReadAsync(row, token);
        ViewDownloadRequested?.Invoke(jobId);
    }

    public async Task MarkReadAsync(NotificationRowViewModel row, CancellationToken token = default)
    {
        if (!row.IsUnread) return;
        try
        {
            await repository.MarkReadAsync(row.Id, token);
            var index = Items.IndexOf(row);
            if (index >= 0) Items[index] = row.MarkedRead();
            await UpdateUnreadAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { StatusMessage = "The notification could not be marked read."; }
    }

    public async Task DismissAsync(NotificationRowViewModel row, CancellationToken token = default)
    {
        try
        {
            await repository.DismissAsync(row.Id, token);
            Items.Remove(row);
            await UpdateUnreadAsync(token);
            UpdateEmptyState();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { StatusMessage = "The notification could not be dismissed."; }
    }

    public async Task MarkAllReadAsync(CancellationToken token = default)
    {
        try
        {
            await repository.MarkAllReadAsync(token);
            for (var index = 0; index < Items.Count; index++) Items[index] = Items[index].MarkedRead();
            await UpdateUnreadAsync(token);
            StatusMessage = "All notifications marked read.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { StatusMessage = "Notifications could not be marked read."; }
    }

    public async Task DismissAllAsync(CancellationToken token = default)
    {
        try
        {
            await repository.DismissAllAsync(token);
            Items.Clear();
            HasMore = false;
            await UpdateUnreadAsync(token);
            UpdateEmptyState();
            StatusMessage = "Notifications cleared.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { StatusMessage = "Notifications could not be cleared."; }
    }

    private async Task LoadNextPageCoreAsync(CancellationToken token)
    {
        var page = await repository.ReadPageAsync(50, _nextCursor, false, token);
        foreach (var entry in page) Items.Add(new NotificationRowViewModel(entry));
        if (page.Count > 0) _nextCursor = page[^1].Id;
        HasMore = page.Count == 50;
        await UpdateUnreadAsync(token);
        UpdateEmptyState();
    }

    private async Task UpdateUnreadAsync(CancellationToken token)
    {
        UnreadCount = await repository.CountUnreadAsync(token);
        OnPropertyChanged(nameof(UnreadSummary));
    }

    private void UpdateEmptyState()
    {
        OnPropertyChanged(nameof(HasItems));
        EmptyMessage = Items.Count == 0 ? "No notifications yet. Transfer updates will appear here." : "";
    }
}
