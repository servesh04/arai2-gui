using System.Text.Json.Serialization;

namespace AriaGui.Application.Downloads;

public enum AppTheme { System, Light, Dark }
public sealed record UserPreferences(AppTheme Theme = AppTheme.System, string DefaultFolder = "", DesktopLayout? Layout = null)
{
    [JsonIgnore] public DesktopLayout EffectiveLayout => Layout ?? new();
    public void Validate()
    {
        if (!Enum.IsDefined(Theme) || DefaultFolder is null || DefaultFolder.Length > 32760 || (!string.IsNullOrEmpty(DefaultFolder)
            && (!Path.IsPathFullyQualified(DefaultFolder) || DefaultFolder.IndexOfAny(['\r', '\n']) >= 0)))
            throw new DownloadRequestException("Choose a theme and an absolute destination folder.");
        EffectiveLayout.Validate();
    }
}
public sealed record DownloadColumnLayout(DownloadSortColumn Column, double Width, bool Visible = true);
public sealed record DesktopLayout(IReadOnlyList<DownloadColumnLayout>? Columns = null, bool CompactRows = true,
    double SidebarWidth = 160, double DetailsHeight = 180, bool SidebarVisible = true, bool DetailsVisible = true, bool AnimationsEnabled = true,
    DownloadTableSizingMode TableSizingMode = DownloadTableSizingMode.Fit)
{
    public const double MinimumColumnWidth = 60;
    // A single column can use 600 DIPs; wider data remains available through tooltip/details.
    public const double MaximumColumnWidth = 600;
    // Recover finite legacy widths without discarding unrelated user preferences.
    // Loading is read-only; the next explicit save writes the normalized values.
    public DesktopLayout NormalizeSavedColumnWidths() => Columns is null ? this : this with
    {
        Columns = Columns.Select(c => c is not null && double.IsFinite(c.Width)
            ? c with { Width = Math.Clamp(c.Width, MinimumColumnWidth, MaximumColumnWidth) } : c!).ToArray()
    };
    [JsonIgnore] public IReadOnlyList<DownloadColumnLayout> EffectiveColumns => Columns ?? [new(DownloadSortColumn.Name, 280), new(DownloadSortColumn.Size, 140),
        new(DownloadSortColumn.Progress, 150), new(DownloadSortColumn.Status, 140), new(DownloadSortColumn.Speed, 110), new(DownloadSortColumn.Eta, 130)];
    public void Validate()
    {
        if (!Enum.IsDefined(TableSizingMode) || !double.IsFinite(SidebarWidth) || SidebarWidth is < 120 or > 400 || !double.IsFinite(DetailsHeight) || DetailsHeight is < 120 or > 600
            || EffectiveColumns.Count != 6 || EffectiveColumns.Any(c => c is null || !Enum.IsDefined(c.Column) || c.Column == DownloadSortColumn.Default
                || !double.IsFinite(c.Width) || c.Width < MinimumColumnWidth || c.Width > MaximumColumnWidth) || EffectiveColumns.Select(c => c.Column).Distinct().Count() != 6
            || !EffectiveColumns.Any(c => c.Column == DownloadSortColumn.Name && c.Visible))
            throw new DownloadRequestException("Choose a valid column layout and pane sizes; the Name column must remain visible.");
    }
}
public interface IUserPreferencesRepository
{
    Task<UserPreferences> LoadAsync(CancellationToken token);
    Task SaveAsync(UserPreferences preferences, CancellationToken token);
}
