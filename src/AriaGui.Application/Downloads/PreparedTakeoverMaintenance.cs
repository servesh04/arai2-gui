namespace AriaGui.Application.Downloads;

// Housekeeping only: does not register takeover intake or authorize any browser side effect.
public sealed class PreparedTakeoverMaintenance(IPreparedTakeoverRepository repository, TimeProvider clock, TimeSpan? interval = null)
{
    public async Task RunAsync(CancellationToken lifetime, Func<Task>? reportFailure = null)
    {
        using var timer = new PeriodicTimer(interval ?? TimeSpan.FromSeconds(10), clock);
        try
        {
            do
            {
                try { await repository.CleanupPreparedTakeoversAsync(lifetime).ConfigureAwait(false); }
                catch (DownloadStorageException) { if (reportFailure is not null) await reportFailure().ConfigureAwait(false); }
            } while (await timer.WaitForNextTickAsync(lifetime).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
}
