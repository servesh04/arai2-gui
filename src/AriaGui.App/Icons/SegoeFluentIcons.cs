using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AriaGui.App.Icons;

public static class SegoeFluentIcons
{
    private static readonly IReadOnlyDictionary<string, string> Glyphs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Add"] = "\uE710", ["Download"] = "\uE896", ["History"] = "\uE81C",
        ["Notifications"] = "\uE7F4", ["Settings"] = "\uE713", ["List"] = "\uEA37",
        ["Play"] = "\uE768", ["Pause"] = "\uE769", ["Retry"] = "\uE72C",
        ["Delete"] = "\uE74D", ["Search"] = "\uE721", ["Filter"] = "\uE71C",
        ["Speed"] = "\uEC4A", ["Status"] = "\uE9D9", ["ChevronDown"] = "\uE70D"
    };

    public static string Get(string name) => Glyphs.TryGetValue(name, out var glyph) ? glyph : "\uE946";
}

public sealed class FluentIcon : FontIcon
{
    public static readonly DependencyProperty IconNameProperty = DependencyProperty.Register(
        nameof(IconName), typeof(string), typeof(FluentIcon), new PropertyMetadata("Status", OnIconNameChanged));

    public FluentIcon() => FontFamily = new FontFamily("Segoe Fluent Icons");
    public string IconName { get => (string)GetValue(IconNameProperty); set => SetValue(IconNameProperty, value); }
    private static void OnIconNameChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((FluentIcon)sender).Glyph = SegoeFluentIcons.Get((string)args.NewValue);
}
