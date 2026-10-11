using AriaGui.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace AriaGui.App.Views;

public sealed partial class PreferencesDialog : ContentDialog
{
    public PreferencesViewModel Preferences { get; }
    public DownloadsViewModel ViewModel { get; }
    public PreferencesDialog(PreferencesViewModel preferences, DownloadsViewModel viewModel)
    { Preferences = preferences; ViewModel = viewModel; InitializeComponent(); }
}
