using AriaGui.Core.Downloads;

namespace AriaGui.Application.Downloads;

public enum DownloadFilter { All, Active, Waiting, Paused, Recovery, Completed, Failed, Removed }
public enum DownloadSortColumn { Default, Name, Size, Progress, Status, Speed, Eta }
public sealed record HistoryQuery(string Search = "", DownloadFilter Filter = DownloadFilter.All, int Page = 0, int PageSize = 50,
    DownloadSortColumn SortColumn = DownloadSortColumn.Default, bool SortDescending = false)
{
    public void Validate()
    {
        if (Search is null || Search.Length > 200 || !Enum.IsDefined(Filter) || !Enum.IsDefined(SortColumn) || Page is < 0 or > 1_000_000 || PageSize is < 1 or > 100)
            throw new DownloadRequestException("Choose a valid history page and a search of at most 200 characters.");
    }
}
public sealed record HistoryPage(IReadOnlyList<DownloadEntry> Entries, int Total, int Page, int PageSize)
{
    public bool HasNext => (long)(Page + 1) * PageSize < Total;
}
public static class DownloadBrowsing
{
    public const int RecentLimit = 100;
    public static bool IsTerminal(DownloadStatus status) => status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Removed;
    public static bool Matches(DownloadEntry entry, string search, DownloadFilter filter) =>
        (entry.Job.DisplayName.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
         || entry.Job.Source.AbsoluteUri.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
         || entry.Job.Destination.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)) && (filter switch
        {
            DownloadFilter.All => true, DownloadFilter.Active => entry.Progress.Status == DownloadStatus.Downloading,
            DownloadFilter.Waiting => entry.Progress.Status == DownloadStatus.Queued, DownloadFilter.Paused => entry.Progress.Status == DownloadStatus.Paused,
            DownloadFilter.Recovery => entry.Progress.Status == DownloadStatus.Interrupted,
            DownloadFilter.Completed => entry.Progress.Status == DownloadStatus.Completed, DownloadFilter.Failed => entry.Progress.Status == DownloadStatus.Failed,
            DownloadFilter.Removed => entry.Progress.Status == DownloadStatus.Removed, _ => false
        });
    public static IReadOnlyList<DownloadEntry> RetainRecent(IEnumerable<DownloadEntry> entries)
    {
        var rows = entries.ToArray();
        return rows.Where(row => !IsTerminal(row.Progress.Status) || row.PendingOperation is not null)
            .Concat(rows.Where(row => IsTerminal(row.Progress.Status) && row.PendingOperation is null)
                .OrderByDescending(row => row.ObservedAt ?? row.Attempt.StartedAt).Take(RecentLimit))
            .OrderBy(row => row.Job.CreatedAt).ToArray();
    }
}
