using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace AriaGui.App.Views.Downloads;
public sealed partial class EmptyState : UserControl
{
    public EmptyState() => InitializeComponent();
    public void SetVisible(bool visible) => EmptyRoot.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
}
