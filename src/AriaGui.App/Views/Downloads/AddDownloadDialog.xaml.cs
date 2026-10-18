using AriaGui.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace AriaGui.App.Views.Downloads;

public sealed partial class AddDownloadDialog : ContentDialog
{
    public DownloadsViewModel ViewModel { get; }
    private IReadOnlyList<string> _urls = [];
    private bool _folderValid;

    public AddDownloadDialog(DownloadsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    private async void DialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        ViewModel.IsAddExpanded = true;
        await PrefillClipboardAsync();
        ValidateFolder();
        ValidateUrls();
        UrlsBox.Focus(FocusState.Programmatic);
        UrlsBox.SelectAll();
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
        _urls = UrlsBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var invalid = _urls.Where(url => !IsSupportedUrl(url)).ToArray();
        UrlError.Text = _urls.Count == 0 ? "Enter at least one URL." : invalid.Length > 0 ? "Use one supported http, https, or ftp URL per line." : string.Empty;
        UrlError.Visibility = UrlError.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        FileNameBox.IsEnabled = _urls.Count <= 1;
        FileNameHint.Visibility = _urls.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        IsPrimaryButtonEnabled = _urls.Count > 0 && invalid.Length == 0 && _folderValid;
    }

    private static bool IsSupportedUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "ftp";

    private void ValidateFolder()
    {
        var path = FolderBox.Text.Trim();
        _folderValid = Directory.Exists(path);
        if (!_folderValid)
        {
            FolderStatus.Text = "Choose an existing, writable folder.";
        }
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
            catch
            {
                _folderValid = false;
                FolderStatus.Text = "The folder could not be checked.";
            }
        }
        IsPrimaryButtonEnabled = _urls.Count > 0 && _urls.All(IsSupportedUrl) && _folderValid;
    }

    private async void AddClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        ValidateUrls(); ValidateFolder();
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
}
