using AriaGui.Application.Downloads;
using AriaGui.Application.Notifications;
using AriaGui.Core.Downloads;
using AriaGui.Infrastructure.Persistence;

namespace AriaGui.Infrastructure.Tests;

public sealed class NotificationPolicyTests
{
    [Fact]
    public async Task N3_legacy_preferences_keep_private_desktop_defaults_and_round_trip_controls()
    {
        var root = Path.Combine(Path.GetTempPath(), "AriaGUI.NotificationPolicyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "preferences.json");
        try
        {
            const string legacy = "{\"Theme\":2,\"DefaultFolder\":\"\"}";
            await File.WriteAllTextAsync(path, legacy);
            var repository = new JsonUserPreferencesRepository(path);
            var loaded = await repository.LoadAsync(default);
            Assert.Equal(legacy, await File.ReadAllTextAsync(path));
            Assert.True(loaded.EffectiveNotifications.HideFileNames);
            Assert.True(loaded.EffectiveNotifications.OnlyWhenInactive);
            var selected = new NotificationPreferences(CompletedEnabled: false, HideFileNames: false);
            await repository.SaveAsync(loaded with { Notifications = selected }, default);
            Assert.Equal(selected, (await repository.LoadAsync(default)).EffectiveNotifications);
        }
        finally
        {
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AriaGUI.NotificationPolicyTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe cleanup.");
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void N3_policy_respects_category_window_privacy_and_opaque_activation()
    {
        var now = DateTimeOffset.UtcNow;
        var entry = Event(1, NotificationKind.TransferCompleted, now.AddSeconds(-10));
        var defaults = new NotificationPreferences();
        Assert.Equal(DesktopDecision.Deliver, NotificationPolicy.Decide(entry, defaults, false, now));
        Assert.Equal(DesktopDecision.Suppress, NotificationPolicy.Decide(entry, defaults, true, now));
        Assert.Equal(DesktopDecision.Suppress, NotificationPolicy.Decide(entry,
            defaults with { CompletedEnabled = false }, false, now));
        Assert.Equal(DesktopDecision.Suppress, NotificationPolicy.Decide(entry with
            { CreatedAt = now.AddMinutes(-6) }, defaults, false, now));
        Assert.Equal(DesktopDecision.Defer, NotificationPolicy.Decide(entry with
            { CreatedAt = now.AddSeconds(-1) }, defaults, false, now));
        Assert.DoesNotContain("private.zip", NotificationPolicy.Compose([entry], defaults).Body);
        Assert.Equal("private.zip", NotificationPolicy.Compose([entry], defaults with { HideFileNames = false }).Body);
        var argument = NotificationPolicy.Compose([entry], defaults).ActivationArgument;
        Assert.True(NotificationActivation.TryParse(argument, out var target));
        Assert.Equal(entry.Id, target!.EventId);
        Assert.False(NotificationActivation.TryParse("notifications/1/../../secret", out _));
        Assert.False(NotificationActivation.TryParse("notifications/0", out _));
    }

    [Fact]
    public async Task N3_coordinator_groups_bursts_suppresses_stale_and_uncertain_delivery_without_duplicate()
    {
        var now = DateTimeOffset.UtcNow;
        var repository = new MemoryNotifications([
            Event(1, NotificationKind.TransferCompleted, now.AddSeconds(-10)),
            Event(2, NotificationKind.TransferCompleted, now.AddSeconds(-9)),
            Event(3, NotificationKind.TransferFailed, now.AddMinutes(-6)),
            Event(4, NotificationKind.TransferFailed, now.AddSeconds(-8)) with { DeliveryState = NotificationDeliveryState.Sending },
            Event(5, NotificationKind.TransferFailed, now.AddSeconds(-1))
        ]);
        var sink = new MemoryDesktop();
        var coordinator = new NotificationCoordinator(repository, sink, new FixedClock(now));
        await coordinator.ProcessPendingAsync(new NotificationPreferences(), false);
        Assert.Single(sink.Messages);
        Assert.Equal(NotificationActivation.InboxArgument, sink.Messages[0].ActivationArgument);
        Assert.Equal(NotificationDeliveryState.Delivered, repository.State(1));
        Assert.Equal(NotificationDeliveryState.Suppressed, repository.State(2));
        Assert.Equal(NotificationDeliveryState.Suppressed, repository.State(3));
        Assert.Equal(NotificationDeliveryState.Suppressed, repository.State(4));
        Assert.Equal(NotificationDeliveryState.Pending, repository.State(5));
        await coordinator.ProcessPendingAsync(new NotificationPreferences(), false);
        Assert.Single(sink.Messages);
        Assert.Equal(5, repository.Entries.Count);
    }

    private static NotificationEntry Event(long id, NotificationKind kind, DateTimeOffset at) =>
        new(id, kind, TransferKind.Http, Guid.NewGuid(), Guid.NewGuid(), "private.zip", "detail", at,
            null, null, NotificationDeliveryState.Pending);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MemoryDesktop : INotificationDesktopSink
    {
        public List<NotificationDesktopMessage> Messages { get; } = [];
        public Task<bool> ShowAsync(NotificationDesktopMessage message, CancellationToken token)
        {
            Messages.Add(message);
            return Task.FromResult(true);
        }
    }

    private sealed class MemoryNotifications(IEnumerable<NotificationEntry> entries) : INotificationRepository
    {
        public Dictionary<long, NotificationEntry> Entries { get; } = entries.ToDictionary(entry => entry.Id);
        public NotificationDeliveryState State(long id) => Entries[id].DeliveryState;
        public Task InitializeAsync(CancellationToken token) => Task.CompletedTask;
        public Task<IReadOnlyList<NotificationEntry>> ReadPageAsync(int pageSize, long? beforeId, bool unreadOnly, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<NotificationEntry>>(Entries.Values.Where(entry => entry.DismissedAt is null
                && (!unreadOnly || entry.ReadAt is null) && (beforeId is null || entry.Id < beforeId))
                .OrderByDescending(entry => entry.Id).Take(pageSize).ToArray());
        public Task<bool> TryClaimDesktopAsync(long id, CancellationToken token)
        {
            if (Entries[id].DeliveryState != NotificationDeliveryState.Pending) return Task.FromResult(false);
            Entries[id] = Entries[id] with { DeliveryState = NotificationDeliveryState.Sending };
            return Task.FromResult(true);
        }
        public Task FinishDesktopAsync(long id, NotificationDeliveryState result, CancellationToken token)
        {
            if (Entries[id].DeliveryState == NotificationDeliveryState.Sending)
                Entries[id] = Entries[id] with { DeliveryState = result };
            return Task.CompletedTask;
        }
        public Task<NotificationEntry?> TryAppendAsync(NotificationDraft draft, CancellationToken token) => throw new NotSupportedException();
        public Task<NotificationEntry?> GetAsync(long id, CancellationToken token) => throw new NotSupportedException();
        public Task<int> CountUnreadAsync(CancellationToken token) => throw new NotSupportedException();
        public Task MarkReadAsync(long id, CancellationToken token) => throw new NotSupportedException();
        public Task MarkAllReadAsync(CancellationToken token) => throw new NotSupportedException();
        public Task DismissAsync(long id, CancellationToken token) => throw new NotSupportedException();
        public Task DismissAllAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<int> PruneReadAsync(DateTimeOffset olderThan, int maxVisible, CancellationToken token) => throw new NotSupportedException();
    }
}
