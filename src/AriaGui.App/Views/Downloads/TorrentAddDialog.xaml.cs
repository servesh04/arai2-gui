using AriaGui.Core.Downloads;
using AriaGui.Application.Engine;
using AriaGui.Application.Downloads;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace AriaGui.App.Views.Downloads;

public sealed partial class TorrentAddDialog : ContentDialog
{
    private readonly WindowId _windowId;
    private readonly string _privateRuntimeRoot;
    private readonly Func<byte[], IReadOnlyList<int>, string, bool, CancellationToken, Task<Guid>> _submit;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<int, TreeViewNode> _leaves = [];
    private TorrentManifest? _manifest;
    private byte[]? _metainfo;
    private bool _populating;
    public Guid? AddedJobId { get; private set; }

    public TorrentAddDialog(WindowId windowId, string destinationRoot, string privateRuntimeRoot,
        bool privacyNoticeAccepted,
        Func<byte[], IReadOnlyList<int>, string, bool, CancellationToken, Task<Guid>> submit)
    {
        _windowId = windowId;
        _privateRuntimeRoot = privateRuntimeRoot;
        _submit = submit;
        InitializeComponent();
        FolderBox.Text = destinationRoot;
        PrivacyConsent.IsChecked = privacyNoticeAccepted;
        PrivacyNotice.IsOpen = !privacyNoticeAccepted;
        PrivacyConsent.Visibility = privacyNoticeAccepted ? Visibility.Collapsed : Visibility.Visible;
        IsPrimaryButtonEnabled = false;
        Closed += (_, _) => _lifetime.Cancel();
    }

    private void DialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        FolderBox.Focus(FocusState.Programmatic);
        UpdateSummary();
    }

    private async void ChooseTorrentClick(object sender, RoutedEventArgs args)
    {
        try
        {
            var picker = new FileOpenPicker(_windowId) { CommitButtonText = "Preview torrent" };
            picker.FileTypeFilter.Add(".torrent");
            var chosen = await picker.PickSingleFileAsync().AsTask(_lifetime.Token);
            if (chosen is null) return;
            var file = new FileInfo(chosen.Path);
            if (file.Length is <= 0 or > TorrentManifestParser.MaximumMetadataBytes)
                throw new InvalidDataException("Torrent metadata is empty or too large.");
            await using var source = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var metainfo = new byte[(int)file.Length];
            await source.ReadExactlyAsync(metainfo, _lifetime.Token);
            var manifest = await TorrentManifestParser.ParseAsync(new MemoryStream(metainfo, writable: false), _lifetime.Token);
            _manifest = manifest;
            _metainfo = metainfo;
            TorrentName.Text = manifest.Name;
            TorrentSummary.Text = $"{manifest.Files.Count} files · {FormatBytes(manifest.TotalBytes)} · {manifest.Trackers.Count} tracker(s)";
            PopulateTree(manifest);
            SetError(null);
            UpdateSummary();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException)
        { SetError("This torrent file could not be previewed. Choose a valid local BitTorrent v1 .torrent file."); }
    }

    private void PopulateTree(TorrentManifest manifest)
    {
        _populating = true;
        FileTree.RootNodes.Clear();
        _leaves.Clear();
        var folders = new Dictionary<string, TreeViewNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            var parts = file.RelativePath.Split(Path.DirectorySeparatorChar);
            TreeViewNode? parent = null;
            for (var index = 0; index < parts.Length - 1; index++)
            {
                var path = string.Join(Path.DirectorySeparatorChar, parts.Take(index + 1));
                if (!folders.TryGetValue(path, out var folder))
                {
                    folder = new TreeViewNode { Content = new TorrentTreeLabel(parts[index], null, 0), IsExpanded = true };
                    if (parent is null) FileTree.RootNodes.Add(folder);
                    else parent.Children.Add(folder);
                    folders[path] = folder;
                }
                parent = folder;
            }
            var leaf = new TreeViewNode { Content = new TorrentTreeLabel(parts[^1], file.Index, file.Length) };
            if (parent is null) FileTree.RootNodes.Add(leaf);
            else parent.Children.Add(leaf);
            _leaves[file.Index] = leaf;
        }
        _populating = false;
        SelectAllButton.IsEnabled = true;
        ClearButton.IsEnabled = true;
    }

    private void SelectAllClick(object sender, RoutedEventArgs args) { FileTree.SelectAll(); UpdateSummary(); }
    private void ClearSelectionClick(object sender, RoutedEventArgs args)
    {
        FileTree.SelectedNodes.Clear();
        UpdateSummary();
    }
    private void FileSelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    { if (!_populating) UpdateSummary(); }
    private void FolderChanged(object sender, TextChangedEventArgs args) => UpdateSummary();
    private void ConsentChanged(object sender, RoutedEventArgs args) => UpdateSummary();

    private async void ChooseFolderClick(object sender, RoutedEventArgs args)
    {
        try
        {
            var picker = new FolderPicker(_windowId) { CommitButtonText = "Use this folder" };
            var chosen = await picker.PickSingleFolderAsync().AsTask(_lifetime.Token);
            if (chosen is not null) FolderBox.Text = chosen.Path;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { SetError("The destination folder picker could not open. Enter an existing folder instead."); }
    }

    private void UpdateSummary()
    {
        if (SelectionSummary is null || FolderBox is null || PrivacyConsent is null) return;
        var selected = _leaves.Where(pair => FileTree.SelectedNodes.Contains(pair.Value)).Select(pair => pair.Key).ToArray();
        var bytes = _manifest is null ? 0 : selected.Sum(index => _manifest.Files[index - 1].Length);
        SelectionSummary.Text = selected.Length == 0 ? "No files selected." : $"{selected.Length} selected · {FormatBytes(bytes)}";
        var folder = FolderBox.Text.Trim();
        var validFolder = Directory.Exists(folder) && Path.IsPathFullyQualified(folder);
        var enoughSpace = false;
        if (validFolder && _manifest is not null)
        {
            try
            {
                var publicDrive = Path.GetPathRoot(Path.GetFullPath(folder))!;
                var privateDrive = Path.GetPathRoot(Path.GetFullPath(_privateRuntimeRoot))!;
                var available = new DriveInfo(publicDrive).AvailableFreeSpace;
                var publicNeed = bytes;
                var privateNeed = _manifest.TotalBytes;
                enoughSpace = string.Equals(publicDrive, privateDrive, StringComparison.OrdinalIgnoreCase)
                    ? available >= checked(privateNeed + publicNeed)
                    : available >= publicNeed && new DriveInfo(privateDrive).AvailableFreeSpace >= privateNeed;
                var remaining = string.Equals(publicDrive, privateDrive, StringComparison.OrdinalIgnoreCase)
                    ? available - privateNeed - publicNeed : available - publicNeed;
                SpaceSummary.Text = $"{FormatBytes(available)} free on destination · "
                    + (enoughSpace ? $"about {FormatBytes(Math.Max(0, remaining))} after payload and publication"
                        : "more space is needed for private payload and published files");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
            { SpaceSummary.Text = "Free space could not be checked."; }
        }
        else SpaceSummary.Text = validFolder ? "Choose a torrent to estimate disk use." : "Choose an existing destination folder.";
        IsPrimaryButtonEnabled = _metainfo is not null && selected.Length > 0 && validFolder && enoughSpace
            && PrivacyConsent.IsChecked == true;
    }

    private async void AddClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        UpdateSummary();
        if (!IsPrimaryButtonEnabled || _metainfo is null) return;
        var indices = _leaves.Where(pair => FileTree.SelectedNodes.Contains(pair.Value)).Select(pair => pair.Key).Order().ToArray();
        try
        {
            IsPrimaryButtonEnabled = false;
            AddedJobId = await _submit(_metainfo, indices, FolderBox.Text.Trim(), PrivacyConsent.IsChecked == true,
                _lifetime.Token);
            Hide();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or EngineException or DownloadStorageException)
        { SetError("The torrent could not be added. Its selection was kept so you can review the destination and try again."); UpdateSummary(); }
    }

    private void SetError(string? message)
    {
        ErrorText.Text = message ?? string.Empty;
        ErrorText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }
    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024 * 1024
        ? $"{bytes / (1024d * 1024 * 1024):0.##} GiB" : bytes >= 1024 * 1024
            ? $"{bytes / (1024d * 1024):0.#} MiB" : bytes >= 1024 ? $"{bytes / 1024d:0.#} KiB" : $"{bytes} B";

    private sealed record TorrentTreeLabel(string Name, int? FileIndex, long Length)
    {
        public override string ToString() => FileIndex is null ? Name : $"{Name} · {FormatBytes(Length)}";
    }
}
