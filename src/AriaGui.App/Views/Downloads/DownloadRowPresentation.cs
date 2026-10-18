using System.Globalization;
using System.Text.RegularExpressions;
using AriaGui.App.ViewModels;
using AriaGui.Core.Downloads;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace AriaGui.App.Views.Downloads;

public sealed partial class MeasuredMiddleEllipsisTextBlock : UserControl
{
    public static readonly DependencyProperty FullTextProperty = DependencyProperty.Register(
        nameof(FullText), typeof(string), typeof(MeasuredMiddleEllipsisTextBlock),
        new PropertyMetadata(string.Empty, OnFullTextChanged));

    private static readonly Regex VersionPattern = new(@"(?<!\d)\d+(?:\.\d+){1,4}(?:[-._]?[A-Za-z0-9]+)?", RegexOptions.Compiled);
    private readonly TextBlock _text = new() { TextTrimming = TextTrimming.None, VerticalAlignment = VerticalAlignment.Center };

    public MeasuredMiddleEllipsisTextBlock()
    {
        Content = _text;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        SizeChanged += (_, _) => UpdateDisplayedText();
        Loaded += (_, _) => UpdateDisplayedText();
    }

    public string FullText
    {
        get => (string)GetValue(FullTextProperty);
        set => SetValue(FullTextProperty, value);
    }

    private static void OnFullTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((MeasuredMiddleEllipsisTextBlock)sender).UpdateDisplayedText();

    private void UpdateDisplayedText()
    {
        var full = FullText ?? string.Empty;
        if (string.IsNullOrEmpty(full) || ActualWidth <= 0 || Fits(full))
        {
            _text.Text = full;
            return;
        }

        var extensionLength = ExtensionLength(full);
        var version = VersionPattern.Match(full);
        var mandatoryHead = version.Success && version.Index < full.Length * .7
            ? version.Index + version.Length
            : Math.Min(Math.Max(4, full.Length / 3), Math.Max(0, full.Length - extensionLength - 1));
        var separator = full.LastIndexOfAny(['-', '_', '.', ' '], Math.Max(0, full.Length - extensionLength - 1));
        var mandatoryTail = Math.Max(extensionLength, separator >= 0 ? full.Length - separator - 1 : extensionLength);

        mandatoryHead = Math.Clamp(mandatoryHead, 1, Math.Max(1, full.Length - mandatoryTail - 1));
        mandatoryTail = Math.Clamp(mandatoryTail, extensionLength, Math.Max(extensionLength, full.Length - mandatoryHead - 1));
        var best = Build(full, mandatoryHead, mandatoryTail);

        for (var retained = mandatoryHead + mandatoryTail + 1; retained < full.Length; retained++)
        {
            var extra = retained - mandatoryHead - mandatoryTail;
            var head = Math.Min(full.Length - mandatoryTail - 1, mandatoryHead + (int)Math.Ceiling(extra * .65));
            var tail = Math.Min(full.Length - head - 1, mandatoryTail + extra - (head - mandatoryHead));
            var candidate = Build(full, head, tail);
            if (!Fits(candidate)) break;
            best = candidate;
        }

        if (!Fits(best) && version.Success)
        {
            var compactVersion = version.Value;
            best = $"…{compactVersion}…{full[^Math.Max(1, extensionLength)..]}";
        }
        _text.Text = Fits(best) ? best : $"…{full[^Math.Max(1, extensionLength)..]}";
    }

    private bool Fits(string value)
    {
        var probe = new TextBlock
        {
            Text = value,
            FontFamily = FontFamily,
            FontSize = FontSize,
            FontStretch = FontStretch,
            FontStyle = FontStyle,
            FontWeight = FontWeight,
            CharacterSpacing = CharacterSpacing
        };
        probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Width <= Math.Max(0, ActualWidth - 1);
    }

    private static string Build(string text, int head, int tail) => $"{text[..head]}…{text[^tail..]}";

    private static int ExtensionLength(string name)
    {
        foreach (var extension in new[] { ".tar.gz", ".tar.bz2", ".tar.xz" })
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return extension.Length;
        return Path.GetExtension(name).Length;
    }
}

public sealed class DownloadFileGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var name = value as string ?? string.Empty;
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (extension == ".iso") return "\uE958";
        if (extension is ".exe" or ".msi" or ".msix" or ".appx") return "\uE7B8";
        if (extension is ".zip" or ".gz" or ".7z" or ".rar" or ".bz2" or ".xz") return "\uF012";
        if (extension is ".mp4" or ".mkv" or ".mov" or ".avi" or ".webm") return "\uE8B2";
        if (extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".svg") return "\uE8B9";
        if (extension is ".pdf" or ".doc" or ".docx" or ".txt" or ".rtf" or ".epub") return "\uE8A5";
        return "\uE7C3";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class DownloadRowVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not DownloadItemViewModel item) return Visibility.Collapsed;
        var visible = (parameter as string) switch
        {
            "Failed" => item.RawStatus == DownloadStatus.Failed,
            "FailedReason" => item.RawStatus == DownloadStatus.Failed && !string.IsNullOrWhiteSpace(item.FailureReason),
            "Pause" => item.ShowPause,
            "Resume" => item.ShowResume,
            "Retry" => item.ShowRetry,
            "OpenFile" => item.ShowOpenFile,
            "Pill" => item.RawStatus is DownloadStatus.Queued or DownloadStatus.Paused or DownloadStatus.Interrupted or DownloadStatus.Removed,
            "Plain" => item.RawStatus is DownloadStatus.Downloading or DownloadStatus.Completed,
            _ => false
        };
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class DownloadStatusLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is DownloadItemViewModel item
        ? item.RawStatus switch
        {
            DownloadStatus.Queued => "Waiting",
            DownloadStatus.Downloading => "Downloading",
            DownloadStatus.Completed => "Completed",
            DownloadStatus.Removed => "Stopped",
            DownloadStatus.Interrupted => "Interrupted",
            _ => item.RawStatus.ToString()
        }
        : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class DownloadStatusGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is DownloadItemViewModel item
        ? item.RawStatus switch
        {
            DownloadStatus.Downloading => "\uE896",
            DownloadStatus.Completed => "\uE73E",
            DownloadStatus.Failed => "\uE711",
            DownloadStatus.Queued => "\uE823",
            DownloadStatus.Paused => "\uE769",
            DownloadStatus.Interrupted => "\uE7BA",
            DownloadStatus.Removed => "\uE71A",
            _ => "\uE946"
        }
        : "\uE946";

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class DownloadProgressTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not DownloadItemViewModel item) return string.Empty;
        var total = item.TotalBytes is long bytes ? DownloadItemViewModel.FormatBytes(bytes) : "unknown";
        return $"{DownloadItemViewModel.FormatBytes(item.DownloadedBytes)} of {total}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class DownloadProgressBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var key = value is DownloadItemViewModel item ? item.RawStatus switch
        {
            DownloadStatus.Downloading => "AriaAccentBrush",
            DownloadStatus.Paused => "AriaWarningBrush",
            DownloadStatus.Failed => "AriaDangerBrush",
            DownloadStatus.Completed => "AriaSuccessBrush",
            _ => "AriaTextDisabledBrush"
        } : "AriaTextDisabledBrush";
        return Microsoft.UI.Xaml.Application.Current.Resources[key];
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class DownloadDateLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not DateTimeOffset date) return string.Empty;
        var age = DateTimeOffset.Now - date.ToLocalTime();
        if (age < TimeSpan.FromMinutes(1)) return "Just now";
        if (age < TimeSpan.FromHours(1)) return $"{Math.Max(1, (int)age.TotalMinutes)} min ago";
        if (age < TimeSpan.FromDays(1)) return $"{Math.Max(1, (int)age.TotalHours)} h ago";
        if (age < TimeSpan.FromDays(7)) return $"{Math.Max(1, (int)age.TotalDays)} d ago";
        return date.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class DownloadDateTooltipConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is DateTimeOffset date
        ? date.ToLocalTime().ToString("F", CultureInfo.CurrentCulture)
        : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class DownloadAccessibleNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is DownloadItemViewModel item
        ? $"{item.FileName}, {new DownloadStatusLabelConverter().Convert(item, typeof(string), string.Empty, language)}, {item.ProgressLabel}, {item.SpeedLabel}"
        : "Download";

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
