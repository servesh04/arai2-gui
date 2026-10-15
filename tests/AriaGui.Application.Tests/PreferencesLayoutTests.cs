using System.Xml.Linq;

namespace AriaGui.Application.Tests;

public sealed class PreferencesLayoutTests
{
    [Fact]
    public void Bandwidth_actions_stack_vertically_inside_scrollable_preferences()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "PreferencesDialog.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        Assert.NotNull(document.Descendants(xaml + "ScrollViewer").SingleOrDefault());
        foreach (var command in new[] { "SaveProfileCommand", "DeleteProfileCommand", "AddRuleCommand",
                     "ReplaceRuleCommand", "RemoveRuleCommand", "ApplyOverrideCommand", "ClearOverrideCommand" })
        {
            var button = document.Descendants(xaml + "Button").Single(element =>
                element.Attribute("Command")?.Value.Contains(command, StringComparison.Ordinal) == true);
            Assert.Equal("Stretch", button.Attribute("HorizontalAlignment")?.Value);
            Assert.Equal("Vertical", button.Parent?.Attribute("Orientation")?.Value);
        }
    }
}
