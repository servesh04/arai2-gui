using Microsoft.UI;
using Microsoft.Windows.Storage.Pickers;

namespace AriaGui.App.Services;

public sealed class WinUiFolderPicker : IDownloadFolderPicker
{
    public WindowId WindowId { private get; set; }
    public async Task<string?> ChooseAsync(CancellationToken cancellationToken)
    {
        var picker = new FolderPicker(WindowId) { CommitButtonText = "Choose download folder" };
        var result = await picker.PickSingleFolderAsync().AsTask(cancellationToken);
        return result?.Path;
    }
}
