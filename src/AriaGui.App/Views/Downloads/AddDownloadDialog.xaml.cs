using AriaGui.App.ViewModels;
using AriaGui.Application.Downloads;
using AriaGui.Application.Engine;
using AriaGui.Core.Downloads;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;

namespace AriaGui.App.Views.Downloads;

public sealed partial class AddDownloadDialog : ContentDialog
{
    public DownloadsViewModel ViewModel { get; }
    public Guid? AddedJobId { get; private set; }
    private readonly WindowId _windowId;
    private readonly string _privateRuntimeRoot;
    private readonly string? _initialTorrentPath;
    private readonly Func<byte[], IReadOnlyList<int>, string, bool, CancellationToken, Task<Guid>> _submit;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<int, TreeViewNode> _leaves = [];
    private IReadOnlyList<string> _urls = [];
    private TorrentManifest? _manifest;
    private byte[]? _metainfo;
    private bool _torrentMode;
    private bool _folderValid;
    private string? _validatedFolder;
    private bool _populating;

    public AddDownloadDialog(DownloadsViewModel viewModel, WindowId windowId, string privateRuntimeRoot,
        bool privacyAccepted, Func<byte[], IReadOnlyList<int>, string, bool, CancellationToken, Task<Guid>> submit,
        string? initialTorrentPath = null)
    {
        ViewModel = viewModel;
        _windowId = windowId;
        _privateRuntimeRoot = privateRuntimeRoot;
        _submit = submit;
        _initialTorrentPath = initialTorrentPath;
        InitializeComponent();
        PrivacyConsent.IsChecked = privacyAccepted;
        PrivacyConsent.Visibility = privacyAccepted ? Visibility.Collapsed : Visibility.Visible;
        PrivacyNotice.IsOpen = !privacyAccepted;
        PrivacySummary.Visibility = privacyAccepted ? Visibility.Visible : Visibility.Collapsed;
        Closed += (_, _) => _lifetime.Cancel();
    }

    private async void DialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        ViewModel.IsAddExpanded = true;
        ValidateFolder();
        if (_initialTorrentPath is not null) await LoadTorrentAsync(_initialTorrentPath);
        else
        {
            await PrefillClipboardAsync();
            ValidateUrls();
            UrlsBox.Focus(FocusState.Programmatic);
            UrlsBox.SelectAll();
        }
    }

    private async Task PrefillClipboardAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text)) return;
            var text = (await content.GetTextAsync()).Trim();
            if (IsSupportedUrl(text)) UrlsBox.Text = text;
        }
        catch { }
    }

    private void UrlsChanged(object sender, TextChangedEventArgs args) => ValidateUrls();
    private void FolderChanged(object sender, TextChangedEventArgs args) => ValidateFolder();
    private void ValidateUrls()
    {
        if (_torrentMode) return;
        if (FolderBox?.Text.Trim() != _validatedFolder || !_folderValid) ValidateFolder();
        _urls = UrlsBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var invalid = _urls.Where(url => !IsSupportedUrl(url)).ToArray();
        UrlError.Text = _urls.Count == 0 ? "Enter at least one URL." : invalid.Length > 0 ? "Use one supported http, https, or ftp URL per line." : string.Empty;
        UrlError.Visibility = UrlError.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        FileNameBox.IsEnabled = _urls.Count <= 1;
        FileNameHint.Visibility = _urls.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        IsPrimaryButtonEnabled = _urls.Count > 0 && invalid.Length == 0 && _folderValid;
    }

    private static bool IsSupportedUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "ftp";

    private void ValidateFolder()
    {
        if (FolderBox is null || FolderStatus is null) return;
        var path = FolderBox.Text.Trim();
        _validatedFolder = path;
        _folderValid = Directory.Exists(path);
        if (!_folderValid) FolderStatus.Text = "Choose an existing, writable folder.";
        else
        {
            try
            {
                var probe = Path.Combine(path, $".ariagui-write-{Guid.NewGuid():N}.tmp");
                using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
                var root = Path.GetPathRoot(Path.GetFullPath(path));
                var free = root is null ? 0 : new DriveInfo(root).AvailableFreeSpace;
                FolderStatus.Text = $"Writable · {free / 1_073_741_824d:0.#} GB free";
            }
            catch { _folderValid = false; FolderStatus.Text = "The folder could not be checked."; }
        }
        if (_torrentMode) UpdateTorrentSummary();
        else IsPrimaryButtonEnabled = _urls.Count > 0 && _urls.All(IsSupportedUrl) && _folderValid;
    }

    private async void ChooseTorrentClick(object sender, RoutedEventArgs args)
    {
        try
        {
            var picker = new FileOpenPicker(_windowId) { CommitButtonText = "Preview torrent" };
            picker.FileTypeFilter.Add(".torrent");
            var chosen = await picker.PickSingleFileAsync().AsTask(_lifetime.Token);
            if (chosen is not null) await LoadTorrentAsync(chosen.Path);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { SetTorrentError("The torrent picker could not open. Drop a local .torrent file instead."); }
    }

    private void DialogDragOver(object sender, DragEventArgs args)
    {
        if (!args.DataView.Contains(StandardDataFormats.StorageItems)) return;
        args.AcceptedOperation = DataPackageOperation.Copy;
        args.DragUIOverride.Caption = "Preview torrent";
        args.Handled = true;
    }

    private async void DialogDrop(object sender, DragEventArgs args)
    {
        if (!args.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var files = await args.DataView.GetStorageItemsAsync();
        var torrent = files.FirstOrDefault(file => Path.GetExtension(file.Path).Equals(".torrent", StringComparison.OrdinalIgnoreCase));
        if (torrent is null) return;
        args.Handled = true;
        await LoadTorrentAsync(torrent.Path);
    }

    private async Task LoadTorrentAsync(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Extension.Equals(".torrent", StringComparison.OrdinalIgnoreCase)
                || file.Length is <= 0 or > TorrentManifestParser.MaximumMetadataBytes)
                throw new InvalidDataException("Invalid torrent metadata size.");
            await using var source = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var metainfo = new byte[(int)file.Length];
            await source.ReadExactlyAsync(metainfo, _lifetime.Token);
            var manifest = await TorrentManifestParser.ParseAsync(new MemoryStream(metainfo, writable: false), _lifetime.Token);
            _manifest = manifest;
            _metainfo = metainfo;
            _torrentMode = true;
            PrimaryButtonText = "Add selected files";
            TorrentName.Text = manifest.Name;
            TorrentSummary.Text = $"{manifest.Files.Count} files · {FormatBytes(manifest.TotalBytes)} · {manifest.Trackers.Count} tracker(s)";
            UrlsBox.Visibility = Visibility.Collapsed;
            UrlError.Visibility = Visibility.Collapsed;
            FileNameBox.Visibility = Visibility.Collapsed;
            FileNameHint.Visibility = Visibility.Collapsed;
            StartPaused.Visibility = Visibility.Collapsed;
            StartPausedHint.Visibility = Visibility.Collapsed;
            AdvancedOptions.Visibility = Visibility.Collapsed;
            TorrentSection.Visibility = Visibility.Visible;
            PopulateTree(manifest);
            SetTorrentError(null);
            UpdateTorrentSummary();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException)
        { SetTorrentError("This torrent file could not be previewed. Choose a valid local BitTorrent v1 .torrent file."); }
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
                var relative = string.Join(Path.DirectorySeparatorChar, parts.Take(index + 1));
                if (!folders.TryGetValue(relative, out var folder))
                {
                    folder = new TreeViewNode { Content = new TorrentDialogFileLabel(parts[index], relative, null, 0), IsExpanded = true };
                    if (parent is null) FileTree.RootNodes.Add(folder);
                    else parent.Children.Add(folder);
                    folders[relative] = folder;
                }
                parent = folder;
            }
            var leaf = new TreeViewNode { Content = new TorrentDialogFileLabel(parts[^1], file.RelativePath, file.Index, file.Length) };
            if (parent is null) FileTree.RootNodes.Add(leaf);
            else parent.Children.Add(leaf);
            _leaves[file.Index] = leaf;
        }
        SelectAllFiles();
    }

    private void SelectAllFiles()
    {
        _populating = true;
        try
        {
            FileTree.SelectedNodes.Clear();
            foreach (var leaf in _leaves.Values) FileTree.SelectedNodes.Add(leaf);
        }
        finally { _populating = false; }
        UpdateTorrentSummary();
    }

    private void SelectAllClick(object sender, RoutedEventArgs args) => SelectAllFiles();
    private void ClearSelectionClick(object sender, RoutedEventArgs args) { FileTree.SelectedNodes.Clear(); UpdateTorrentSummary(); }
    private void FileSelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    { if (!_populating) UpdateTorrentSummary(); }
    private void ConsentChanged(object sender, RoutedEventArgs args) => UpdateTorrentSummary();
    private void PrivacyDetailsClick(object sender, RoutedEventArgs args) => PrivacyNotice.IsOpen = true;

    private void UpdateTorrentSummary()
    {
        if (!_torrentMode || SelectionSummary is null || SpaceSummary is null) return;
        var selected = _leaves.Where(pair => FileTree.SelectedNodes.Contains(pair.Value)).Select(pair => pair.Key).ToArray();
        var bytes = _manifest is null ? 0 : selected.Sum(index => _manifest.Files[index - 1].Length);
        SelectionSummary.Text = $"{selected.Length} selected · {FormatBytes(bytes)}";
        var folder = FolderBox.Text.Trim();
        var validFolder = _folderValid && Path.IsPathFullyQualified(folder);
        var enoughSpace = false;
        if (validFolder && _manifest is not null)
        {
            try
            {
                var publicDrive = Path.GetPathRoot(Path.GetFullPath(folder))!;
                var privateDrive = Path.GetPathRoot(Path.GetFullPath(_privateRuntimeRoot))!;
                var available = new DriveInfo(publicDrive).AvailableFreeSpace;
                var privateNeed = _manifest.TotalBytes;
                enoughSpace = string.Equals(publicDrive, privateDrive, StringComparison.OrdinalIgnoreCase)
                    ? available >= checked(privateNeed + bytes)
                    : available >= bytes && new DriveInfo(privateDrive).AvailableFreeSpace >= privateNeed;
                var remaining = string.Equals(publicDrive, privateDrive, StringComparison.OrdinalIgnoreCase)
                    ? available - privateNeed - bytes : available - bytes;
                SpaceSummary.Text = $"{FormatBytes(available)} free · " + (enoughSpace
                    ? $"about {FormatBytes(Math.Max(0, remaining))} after download and publication"
                    : "more space is needed for private payload and published files");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
            { SpaceSummary.Text = "Free space could not be checked."; }
        }
        else SpaceSummary.Text = "Choose an existing destination folder.";
        IsPrimaryButtonEnabled = _metainfo is not null && selected.Length > 0 && validFolder && enoughSpace
            && PrivacyConsent.IsChecked == true;
    }

    private async void AddClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        ValidateFolder();
        if (!IsPrimaryButtonEnabled) return;
        if (_torrentMode)
        {
            if (_metainfo is null) return;
            var indices = _leaves.Where(pair => FileTree.SelectedNodes.Contains(pair.Value)).Select(pair => pair.Key).Order().ToArray();
            try
            {
                IsPrimaryButtonEnabled = false;
                AddedJobId = await _submit(_metainfo, indices, FolderBox.Text.Trim(), PrivacyConsent.IsChecked == true, _lifetime.Token);
                Hide();
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                or InvalidOperationException or EngineException or DownloadStorageException)
            { SetTorrentError("The torrent could not be added. Its selection was kept so you can try again."); UpdateTorrentSummary(); }
            return;
        }
        ValidateUrls();
        if (!IsPrimaryButtonEnabled) return;
        var originalName = ViewModel.FileName;
        foreach (var url in _urls)
        {
            ViewModel.InputUrl = url;
            if (_urls.Count > 1) ViewModel.FileName = string.Empty;
            if (!ViewModel.AddDownloadCommand.CanExecute(null)) return;
            await ViewModel.AddDownloadCommand.ExecuteAsync(null);
            if (ViewModel.HasError) return;
        }
        ViewModel.FileName = originalName;
        Hide();
    }

    private void SetTorrentError(string? message)
    {
        TorrentError.Text = message ?? string.Empty;
        TorrentError.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string FormatBytes(long bytes) => bytes >= 1_073_741_824 ? $"{bytes / 1_073_741_824d:0.##} GiB"
        : bytes >= 1_048_576 ? $"{bytes / 1_048_576d:0.#} MiB" : bytes >= 1024 ? $"{bytes / 1024d:0.#} KiB" : $"{bytes} B";

}

public sealed record TorrentDialogFileLabel(string Name, string RelativePath, int? FileIndex, long Length)
{
    public string SizeLabel => FileIndex is null ? string.Empty : Length >= 1_073_741_824 ? $"{Length / 1_073_741_824d:0.##} GiB"
        : Length >= 1_048_576 ? $"{Length / 1_048_576d:0.#} MiB" : Length >= 1024 ? $"{Length / 1024d:0.#} KiB" : $"{Length} B";
    public override string ToString() => Name;
}
