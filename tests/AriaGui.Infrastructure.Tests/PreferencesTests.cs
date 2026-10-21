using AriaGui.Application.Downloads;
using AriaGui.Infrastructure.Persistence;

namespace AriaGui.Infrastructure.Tests;

public sealed class PreferencesTests
{
    [Fact]
    public async Task Torrent_column_choices_round_trip_after_loading_legacy_preferences()
    {
        var root = Path.Combine(Path.GetTempPath(), "AriaGUI.PreferencesTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "preferences.json");
        try
        {
            const string legacy = "{\"Theme\":2,\"DefaultFolder\":\"\",\"Layout\":{\"CompactRows\":false}}";
            await File.WriteAllTextAsync(path, legacy);
            var repository = new JsonUserPreferencesRepository(path);
            var loaded = await repository.LoadAsync(default);
            Assert.Equal(legacy, await File.ReadAllTextAsync(path));
            Assert.Equal(new TorrentColumnPreferences(), loaded.EffectiveLayout.EffectiveTorrentColumns);
            var choices = new TorrentColumnPreferences(TorrentColumnVisibility.Visible,
                TorrentColumnVisibility.Hidden, TorrentColumnVisibility.Automatic, TorrentColumnVisibility.Visible);
            await repository.SaveAsync(loaded with { Layout = loaded.EffectiveLayout with { TorrentColumns = choices } }, default);
            var roundTrip = await repository.LoadAsync(default);
            Assert.Equal(choices, roundTrip.EffectiveLayout.EffectiveTorrentColumns);
            Assert.False(roundTrip.EffectiveLayout.CompactRows);
        }
        finally
        {
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AriaGUI.PreferencesTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe cleanup.");
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Legacy_outlier_widths_are_clamped_on_load_without_rewriting_the_file_or_losing_other_preferences()
    {
        var root = Path.Combine(Path.GetTempPath(), "AriaGUI.PreferencesTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "preferences.json");
        try
        {
            var columns = new DesktopLayout().EffectiveColumns.Reverse().Select((c, i) => c with
                { Width = i == 0 ? 100000 : i == 1 ? -20 : 200, Visible = c.Column != DownloadSortColumn.Size }).ToArray();
            var legacy = new UserPreferences(AppTheme.Dark, @"C:\PrivateDownloads", new(Columns: columns, SidebarWidth: 220,
                DetailsHeight: 420, CompactRows: false, TableSizingMode: DownloadTableSizingMode.Manual));
            var json = System.Text.Json.JsonSerializer.Serialize(legacy);
            await File.WriteAllTextAsync(path, json);
            var repository = new JsonUserPreferencesRepository(path);
            var loaded = await repository.LoadAsync(default);
            Assert.Equal(json, await File.ReadAllTextAsync(path));
            Assert.Equal(columns.Select(c => c.Column), loaded.EffectiveLayout.EffectiveColumns.Select(c => c.Column));
            Assert.Equal(columns.Select(c => c.Visible), loaded.EffectiveLayout.EffectiveColumns.Select(c => c.Visible));
            Assert.Equal(new double[] { 600, 60, 200, 200, 200, 200 }, loaded.EffectiveLayout.EffectiveColumns.Select(c => c.Width));
            Assert.Equal(AppTheme.Dark, loaded.Theme); Assert.Equal(legacy.DefaultFolder, loaded.DefaultFolder);
            Assert.Equal(220, loaded.EffectiveLayout.SidebarWidth); Assert.Equal(420, loaded.EffectiveLayout.DetailsHeight);
            Assert.False(loaded.EffectiveLayout.CompactRows); Assert.Equal(DownloadTableSizingMode.Manual, loaded.EffectiveLayout.TableSizingMode);
            await repository.SaveAsync(loaded, default);
            Assert.DoesNotContain("100000", await File.ReadAllTextAsync(path));
            Assert.Equal(loaded.EffectiveLayout.EffectiveColumns, (await repository.LoadAsync(default)).EffectiveLayout.EffectiveColumns);
            await Assert.ThrowsAsync<DownloadRequestException>(() => repository.SaveAsync(legacy, default));
        }
        finally
        {
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AriaGUI.PreferencesTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe cleanup.");
            Directory.Delete(root, true);
        }
    }
    [Fact]
    public async Task Preferences_round_trip_and_corrupt_input_is_preserved_without_overriding_defaults()
    {
        var root = Path.Combine(Path.GetTempPath(), "AriaGUI.PreferencesTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "preferences.json"); var repository = new JsonUserPreferencesRepository(path);
        try
        {
            Assert.Equal(new UserPreferences(), await repository.LoadAsync(default));
            var expected = new UserPreferences(AppTheme.Dark, @"C:\Downloads\AriaGUI", TorrentPrivacyNoticeAccepted: true);
            await repository.SaveAsync(expected, default);
            Assert.Equal(expected, await new JsonUserPreferencesRepository(path).LoadAsync(default)); Assert.False(File.Exists(path + ".tmp"));
            await File.WriteAllTextAsync(path, "{\"Theme\":2,\"DefaultFolder\":\"\"}");
            var legacy = await repository.LoadAsync(default); Assert.Equal(AppTheme.Dark, legacy.Theme); legacy.EffectiveLayout.Validate();
            Assert.Equal(DownloadTableSizingMode.Fit, legacy.EffectiveLayout.TableSizingMode);
            var layout = new DesktopLayout(Columns: new DesktopLayout().EffectiveColumns.Reverse().Select(c => c with { Width = 200 }).ToArray(), CompactRows: false, SidebarWidth: 220, DetailsHeight: 420, DetailsVisible: false, TableSizingMode: DownloadTableSizingMode.Manual, DetailsWidth: 480);
            await repository.SaveAsync(legacy with { Layout = layout }, default);
            var roundtrip = await repository.LoadAsync(default); Assert.False(roundtrip.EffectiveLayout.CompactRows); Assert.False(roundtrip.EffectiveLayout.DetailsVisible);
            Assert.Equal(layout.EffectiveColumns, roundtrip.EffectiveLayout.EffectiveColumns);
            Assert.Equal(DownloadTableSizingMode.Manual, roundtrip.EffectiveLayout.TableSizingMode);
            Assert.Equal(420, roundtrip.EffectiveLayout.DetailsHeight);
            Assert.Equal(480, roundtrip.EffectiveLayout.DetailsWidth);
            await File.WriteAllTextAsync(path, "{\"Layout\":{\"SidebarWidth\":220,\"DetailsHeight\":420}}");
            var oldLayout = await repository.LoadAsync(default);
            Assert.Equal(DownloadTableSizingMode.Fit, oldLayout.EffectiveLayout.TableSizingMode);
            Assert.Equal(420, oldLayout.EffectiveLayout.DetailsHeight);
            Assert.Equal(360, oldLayout.EffectiveLayout.DetailsWidth);
            await File.WriteAllTextAsync(path, "{\"Layout\":{\"SidebarWidth\":0,\"DetailsHeight\":0,\"DetailsWidth\":0}}");
            var zeroSizedLayout = await repository.LoadAsync(default);
            Assert.Equal(120, zeroSizedLayout.EffectiveLayout.SidebarWidth);
            Assert.Equal(120, zeroSizedLayout.EffectiveLayout.DetailsHeight);
            Assert.Equal(280, zeroSizedLayout.EffectiveLayout.DetailsWidth);
            Assert.Throws<DownloadRequestException>(() => (layout with { TableSizingMode = (DownloadTableSizingMode)999 }).Validate());
            await File.WriteAllTextAsync(path, "broken-json"); await Assert.ThrowsAsync<DownloadRequestException>(() => repository.LoadAsync(default));
            Assert.Equal("broken-json", await File.ReadAllTextAsync(path));
            await Assert.ThrowsAsync<DownloadRequestException>(() => repository.SaveAsync(new(AppTheme.System, @"C:\" + new string('a', 20000)), default));
            Assert.Equal("broken-json", await File.ReadAllTextAsync(path)); Assert.False(File.Exists(path + ".tmp"));
            var blocked = Path.Combine(root, "blocked"); await File.WriteAllTextAsync(blocked, "preserve");
            await Assert.ThrowsAsync<DownloadRequestException>(() => new JsonUserPreferencesRepository(Path.Combine(blocked, "preferences.json")).SaveAsync(expected, default));
            Assert.Equal("preserve", await File.ReadAllTextAsync(blocked));
            await Assert.ThrowsAsync<DownloadRequestException>(() => repository.SaveAsync(new((AppTheme)999, "relative"), default));
            Assert.Equal("broken-json", await File.ReadAllTextAsync(path));
        }
        finally
        {
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AriaGUI.PreferencesTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe cleanup.");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
