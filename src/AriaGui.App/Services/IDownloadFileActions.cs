namespace AriaGui.App.Services;

public interface IDownloadFileActions
{
    Task OpenFileAsync(string path, CancellationToken token);
    Task OpenFolderAsync(string path, CancellationToken token);
}
