using AriaGui.Application.Downloads;
using AriaGui.Core.Downloads;
using AriaGui.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AriaGui.Infrastructure.Tests;

public sealed class HistorySortingTests
{
    [Fact]
    public async Task Numeric_history_sort_orders_entire_result_before_paging_with_stable_ties()
    {
        var root = Path.Combine(Path.GetTempPath(), "AriaGUI.HistorySortingTests", Guid.NewGuid().ToString("N"));
        var database = Path.Combine(root, "downloads.db"); var repository = new SqliteDownloadRepository(database);
        try
        {
            await repository.InitializeAsync(default);
            using (var db = new DownloadDbContext(new DbContextOptionsBuilder<DownloadDbContext>().UseSqlite($"Data Source={database};Pooling=False").Options))
            {
                for (var i = 0; i < 63; i++)
                {
                    var id = Guid.NewGuid(); var attempt = Guid.NewGuid(); var name = $"file-{62-i:000}.bin"; var time = DateTimeOffset.UtcNow.UtcTicks + i;
                    db.Jobs.Add(new() { Id = id, DisplayName = name, Source = "https://example.com/" + name, Destination = Path.Combine(root, name), DestinationKey = Path.Combine(root, name).ToUpperInvariant(), CreatedAtUtcTicks = time });
                    db.Attempts.Add(new() { Id = attempt, JobId = id, EngineId = (i + 1).ToString("x16"), StartedAtUtcTicks = time, UpdatedAtUtcTicks = time, Status = DownloadStatus.Completed, TotalBytes = (62-i) * 1024, DownloadedBytes = (62-i) * 1024 });
                    db.Files.Add(new() { Id = Guid.NewGuid(), JobId = id, AttemptId = attempt, FileName = name, TotalBytes = (62-i) * 1024 });
                }
                await db.SaveChangesAsync();
            }
            var query = new HistoryQuery(SortColumn: DownloadSortColumn.Size);
            var first = await repository.LoadPageAsync(query, default); var second = await repository.LoadPageAsync(query with { Page = 1 }, default);
            Assert.Equal(63, first.Total); Assert.Equal(50, first.Entries.Count); Assert.Equal(13, second.Entries.Count);
            Assert.Equal(Enumerable.Range(0, 63).Select(i => (long?)i * 1024), first.Entries.Concat(second.Entries).Select(row => row.Progress.TotalBytes));
            var descending = await repository.LoadPageAsync(query with { SortDescending = true }, default); Assert.Equal(62 * 1024, descending.Entries[0].Progress.TotalBytes);
            var names = await repository.LoadPageAsync(query with { SortColumn = DownloadSortColumn.Name }, default); Assert.Equal("file-000.bin", names.Entries[0].Job.DisplayName);
            var repeat = await repository.LoadPageAsync(query, default); Assert.Equal(first.Entries.Select(row => row.Job.Id), repeat.Entries.Select(row => row.Job.Id));
            var tiedQuery = query with { SortColumn = DownloadSortColumn.Progress };
            var tiedFirst = await repository.LoadPageAsync(tiedQuery, default); var tiedSecond = await repository.LoadPageAsync(tiedQuery with { Page = 1 }, default);
            var tiedRepeat = await repository.LoadPageAsync(tiedQuery, default);
            Assert.Equal(tiedFirst.Entries.Select(row => row.Job.Id), tiedRepeat.Entries.Select(row => row.Job.Id));
            Assert.Empty(tiedFirst.Entries.Select(row => row.Job.Id).Intersect(tiedSecond.Entries.Select(row => row.Job.Id)));
            // Every completed row, including the zero-byte file, has 100% raw progress.
            Assert.All(tiedFirst.Entries.Concat(tiedSecond.Entries), row => Assert.Equal(100d, row.Progress.Percentage));
            var tiedNames = tiedFirst.Entries.Concat(tiedSecond.Entries).Select(row => row.Job.DisplayName).ToArray();
            var reverseTies = await repository.LoadPageAsync(tiedQuery with { SortDescending = true }, default);
            Assert.Equal(tiedNames.Take(50), reverseTies.Entries.Select(row => row.Job.DisplayName));
        }
        finally
        {
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AriaGUI.HistorySortingTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe cleanup.");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
