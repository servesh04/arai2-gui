using System.Globalization;
using AriaGui.App.ViewModels;
using AriaGui.Core.Downloads;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace AriaGui.App.Views.Downloads;

public sealed partial class DetailsPane : UserControl
{
    private MenuFlyout? _openMenu;

    public DetailsPane()
    {
        InitializeComponent();
        Inspector.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => CloseOpenFlyouts());
        DetailsHost.DataContextChanged += (_, _) => CloseOpenFlyouts();
        Unloaded += (_, _) => CloseOpenFlyouts();
    }

    public Grid Root => Inspector;
    public ScrollViewer Host => DetailsHost;
    public TextBlock Hint => SelectionHint;
    public StackPanel Metadata => InspectorMetadata;
    public Button OpenFile => InspectorOpenFile;
    public Button OpenFolder => InspectorOpenFolder;
    public event RoutedEventHandler? SingleActionRequested;

    private void SingleActionClick(object sender, RoutedEventArgs args) => SingleActionRequested?.Invoke(sender, args);

    private void CopyUrlClick(object sender, RoutedEventArgs args)
    {
        if (DetailsHost.DataContext is DownloadItemViewModel item) CopyText(item.SourceUrl);
    }

    private void CopyPathClick(object sender, RoutedEventArgs args)
    {
        if (DetailsHost.DataContext is DownloadItemViewModel item) CopyText(item.Destination);
    }

    private static void CopyText(string value)
    {
        var package = new DataPackage();
        package.SetText(value);
        Clipboard.SetContent(package);
    }

    private void DetailsMenuOpening(object sender, object args)
    {
        _openMenu = (MenuFlyout)sender;
        DownloadFlyoutCoordinator.Showing(_openMenu);
    }

    private void DetailsMenuClosed(object sender, object args)
    {
        DownloadFlyoutCoordinator.Closed((FlyoutBase)sender);
        if (ReferenceEquals(_openMenu, sender)) _openMenu = null;
    }

    private async void RemoveClick(object sender, RoutedEventArgs args)
    {
        if (DetailsHost.DataContext is not DownloadItemViewModel item || !item.RemoveCommand.CanExecute(null)) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Remove download?",
            Content = "The entry will be removed from the transfer list. Downloaded files are kept.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && item.RemoveCommand.CanExecute(null))
            item.RemoveCommand.Execute(null);
    }

    private async void CloseClick(object sender, RoutedEventArgs args)
    {
        CloseOpenFlyouts();
        if (DataContext is not DownloadsPage page) return;
        page.Preferences.DetailsVisible = false;
        await page.SaveLayoutAsync();
    }

    private void CloseOpenFlyouts()
    {
        _openMenu?.Hide();
        _openMenu = null;
        DownloadFlyoutCoordinator.HideAll();
        if (FindDescendant<DownloadsTable>(FindAncestor<DownloadsPage>(this))?.List is not { } list) return;
        foreach (var item in list.Items)
            if (list.ContainerFromItem(item) is ListViewItem container) container.ContextFlyout?.Hide();
    }

    private void DetailsHostSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var columns = args.NewSize.Width >= 920 ? 4 : args.NewSize.Width >= 520 ? 2 : 1;
        var cards = new[] { TransferCard, ProgressCard, NetworkCard, GeneralCard };
        for (var index = 0; index < cards.Length; index++)
        {
            Grid.SetColumn(cards[index], index % columns);
            Grid.SetRow(cards[index], index / columns);
        }
    }

    private static T? FindAncestor<T>(DependencyObject? child) where T : DependencyObject
    {
        for (var current = child; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is T match) return match;
        return null;
    }

    private static T? FindDescendant<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent is null) return null;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } descendant) return descendant;
        }
        return null;
    }
}

public sealed class DetailsFileGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        new DownloadFileGlyphConverter().Convert(value, targetType, parameter, language);
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class ProtocolLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (!Uri.TryCreate(value as string, UriKind.Absolute, out var uri)) return "URL";
        return uri.Scheme.ToUpperInvariant();
    }
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class FailureCardVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is DownloadItemViewModel { RawStatus: DownloadStatus.Failed } ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class FailureReasonVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is string reason && !string.IsNullOrWhiteSpace(reason) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class HttpStatusVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is int ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class HttpStatusLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is int status ? $"HTTP {status}" : string.Empty;
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class DownloadTypeLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var extension = Path.GetExtension(value as string ?? string.Empty);
        return string.IsNullOrWhiteSpace(extension) ? "File" : $"{extension.TrimStart('.').ToUpperInvariant()} file";
    }
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class DetailsDateConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        DateTimeOffset date => date.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
        _ => "—"
    };
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
