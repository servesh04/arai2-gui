using AriaGui.Core.Downloads;
using AriaGui.Infrastructure.Aria2;
using AriaGui.Infrastructure.Downloads;
using AriaGui.Infrastructure.Persistence;
using Microsoft.UI.Xaml.Controls;

namespace AriaGui.App.Views.Downloads;

public sealed partial class TorrentStatusView : UserControl
{
    public TorrentStatusView() => InitializeComponent();

    public void Show(StoredTorrentDraft draft, TorrentEngineObservation? observed)
    {
        TorrentName.Text = draft.Manifest.Name;
        var stage = draft.Stage is TorrentStage.Completed or TorrentStage.Failed
            ? draft.Stage : observed?.Stage ?? draft.Stage;
        StageText.Text = stage switch
        {
            TorrentStage.AwaitingSelection => "Ready to start",
            TorrentStage.Waiting => "Waiting in queue",
            TorrentStage.Checking => "Checking files",
            TorrentStage.Downloading => "Downloading",
            TorrentStage.Verifying => "Verifying pieces",
            TorrentStage.Publishing => "Publishing selected files",
            TorrentStage.Completed => "Completed",
            TorrentStage.Failed => "Needs review",
            TorrentStage.Stopped => "Stopped",
            _ => "Checking torrent"
        };
        var total = draft.Selection.SelectedBytes;
        var completed = stage == TorrentStage.Completed ? total : Math.Min(total, observed?.CompletedBytes ?? 0);
        TransferProgress.IsIndeterminate = stage is TorrentStage.Checking or TorrentStage.Verifying;
        TransferProgress.Value = total == 0 ? 0 : 100d * completed / total;
        ProgressText.Text = $"{FormatBytes(completed)} of {FormatBytes(total)} selected · "
            + (stage == TorrentStage.Downloading ? $"{FormatBytes(observed?.DownloadBytesPerSecond ?? 0)}/s" : StageText.Text);
        SelectionText.Text = $"{draft.Selection.FileIndices.Count} selected of {draft.Manifest.Files.Count} files";
        SelectedFiles.ItemsSource = draft.Manifest.Files.Where(file => draft.Selection.FileIndices.Contains(file.Index))
            .Select(file => $"{file.RelativePath} · {FormatBytes(file.Length)}").ToArray();
        DestinationText.Text = $"Publishing to {draft.DestinationRoot}";
        FailureNotice.IsOpen = stage == TorrentStage.Failed;
    }

    public void ShowPublicationProgress(TorrentPublicationProgress progress)
    {
        StageText.Text = "Publishing selected files";
        TransferProgress.IsIndeterminate = false;
        TransferProgress.Value = progress.TotalBytes == 0 ? 100
            : 100d * Math.Min(progress.PublishedBytes, progress.TotalBytes) / progress.TotalBytes;
        ProgressText.Text = $"Publishing files: {FormatBytes(progress.PublishedBytes)} of {FormatBytes(progress.TotalBytes)}";
    }

    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024 * 1024
        ? $"{bytes / (1024d * 1024 * 1024):0.##} GiB" : bytes >= 1024 * 1024
            ? $"{bytes / (1024d * 1024):0.#} MiB" : bytes >= 1024 ? $"{bytes / 1024d:0.#} KiB" : $"{bytes} B";
}
