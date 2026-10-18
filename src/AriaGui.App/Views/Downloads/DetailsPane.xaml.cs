using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AriaGui.App.Views.Downloads;

public sealed partial class DetailsPane : UserControl
{
    public DetailsPane() => InitializeComponent();
    public Grid Root => Inspector;
    public ScrollViewer Host => DetailsHost;
    public TextBlock Hint => SelectionHint;
    public StackPanel Metadata => InspectorMetadata;
    public Button OpenFile => InspectorOpenFile;
    public Button OpenFolder => InspectorOpenFolder;
    public event RoutedEventHandler? SingleActionRequested;
    private void SingleActionClick(object sender, RoutedEventArgs args) => SingleActionRequested?.Invoke(sender, args);
}
