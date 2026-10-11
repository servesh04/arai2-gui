using System.ComponentModel;
using AriaGui.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AriaGui.App.Views;

public sealed partial class AddDownloadDialog : ContentDialog
{
    public DownloadsViewModel ViewModel { get; }
    public AddDownloadDialog(DownloadsViewModel viewModel) { ViewModel = viewModel; InitializeComponent(); UpdateDownloadAvailability(this, EventArgs.Empty); }
    // Successful submission closes through IsAddExpanded; validation errors keep the dialog open.
    private async void DownloadClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (ViewModel.AddDownloadCommand.CanExecute(null)) await ViewModel.AddDownloadCommand.ExecuteAsync(null);
    }
    private void UpdateDownloadAvailability(object? sender, EventArgs args) => IsPrimaryButtonEnabled = ViewModel.AddDownloadCommand.CanExecute(null);
    private void DialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        ViewModel.IsAddExpanded = true;
        ViewModel.PropertyChanged += ViewModelChanged;
        ViewModel.AddDownloadCommand.CanExecuteChanged += UpdateDownloadAvailability;
        UpdateDownloadAvailability(this, EventArgs.Empty);
        UrlBox.Focus(FocusState.Programmatic);
    }
    private void ViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        // The existing add workflow resets this flag only after a successful submission.
        if (args.PropertyName == nameof(ViewModel.IsAddExpanded) && !ViewModel.IsAddExpanded) Hide();
    }
    private void DialogClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        ViewModel.PropertyChanged -= ViewModelChanged;
        ViewModel.AddDownloadCommand.CanExecuteChanged -= UpdateDownloadAvailability;
    }
}
