namespace AriaGui.App.Services;

public interface IDownloadFolderPicker
{
    Task<string?> ChooseAsync(CancellationToken cancellationToken);
}
