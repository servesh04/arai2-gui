namespace AriaGui.Application.Notifications;

public sealed class NotificationCoordinator(INotificationRepository repository, INotificationDesktopSink desktop,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task ProcessPendingAsync(NotificationPreferences preferences, bool isAppActive,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var pending = await LoadPendingAsync(token).ConfigureAwait(false);
            var now = _clock.GetUtcNow();
            foreach (var uncertain in pending.Where(entry => entry.DeliveryState == NotificationDeliveryState.Sending))
                await repository.FinishDesktopAsync(uncertain.Id, NotificationDeliveryState.Suppressed, token).ConfigureAwait(false);

            var candidates = new List<NotificationEntry>();
            foreach (var entry in pending.Where(entry => entry.DeliveryState == NotificationDeliveryState.Pending))
            {
                if (NotificationPolicy.Decide(entry, preferences, isAppActive, now) == DesktopDecision.Suppress)
                {
                    if (await repository.TryClaimDesktopAsync(entry.Id, token).ConfigureAwait(false))
                        await repository.FinishDesktopAsync(entry.Id, NotificationDeliveryState.Suppressed, token).ConfigureAwait(false);
                }
                else candidates.Add(entry);
            }

            foreach (var group in GroupBursts(candidates))
            {
                if (group.Any(entry => NotificationPolicy.Decide(entry, preferences, isAppActive, now) == DesktopDecision.Defer))
                    continue;
                var claimed = new List<NotificationEntry>();
                try
                {
                    foreach (var entry in group)
                    {
                        if (!await repository.TryClaimDesktopAsync(entry.Id, token).ConfigureAwait(false)) break;
                        claimed.Add(entry);
                    }
                    if (claimed.Count != group.Count) continue;
                    var shown = false;
                    try { shown = await desktop.ShowAsync(NotificationPolicy.Compose(group, preferences), token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception) { /* OS delivery failure leaves the in-app entry intact. */ }
                    if (shown)
                    {
                        await repository.FinishDesktopAsync(claimed[0].Id, NotificationDeliveryState.Delivered, CancellationToken.None).ConfigureAwait(false);
                        claimed.RemoveAt(0);
                    }
                }
                finally
                {
                    foreach (var entry in claimed)
                        await repository.FinishDesktopAsync(entry.Id, NotificationDeliveryState.Suppressed, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<NotificationEntry>> LoadPendingAsync(CancellationToken token)
    {
        var pending = new List<NotificationEntry>();
        long? beforeId = null;
        IReadOnlyList<NotificationEntry> page;
        do
        {
            page = await repository.ReadPageAsync(100, beforeId, false, token).ConfigureAwait(false);
            pending.AddRange(page.Where(entry => entry.DeliveryState is NotificationDeliveryState.Pending or NotificationDeliveryState.Sending));
            if (page.Count > 0) beforeId = page[^1].Id;
        } while (page.Count == 100);
        return pending.OrderBy(entry => entry.CreatedAt).ThenBy(entry => entry.Id).ToArray();
    }

    private static IEnumerable<IReadOnlyList<NotificationEntry>> GroupBursts(IReadOnlyList<NotificationEntry> events)
    {
        foreach (var category in events.GroupBy(entry => entry.Kind))
        {
            var ordered = category.OrderBy(entry => entry.CreatedAt).ThenBy(entry => entry.Id).ToArray();
            for (var start = 0; start < ordered.Length;)
            {
                var end = start + 1;
                while (end < ordered.Length && ordered[end].CreatedAt - ordered[start].CreatedAt <= NotificationPolicy.BurstWindow)
                    end++;
                yield return ordered[start..end];
                start = end;
            }
        }
    }
}
