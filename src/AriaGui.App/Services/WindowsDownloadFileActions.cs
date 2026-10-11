using AriaGui.Application.Downloads;
using Windows.Storage;
using Windows.System;

namespace AriaGui.App.Services;

public sealed class WindowsDownloadFileActions : IDownloadFileActions
{
    public async Task OpenFileAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path).AsTask(token);
            if (!await Launcher.LaunchFileAsync(file).AsTask(token)) throw new DownloadRequestException("Windows could not open this file. Choose an app for its file type.");
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        { throw new DownloadRequestException("The completed file is missing or inaccessible. Its saved history was kept."); }
    }
    public async Task OpenFolderAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var folder = await StorageFolder.GetFolderFromPathAsync(path).AsTask(token);
            if (!await Launcher.LaunchFolderAsync(folder).AsTask(token)) throw new DownloadRequestException("Windows could not open the destination folder.");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        { throw new DownloadRequestException("The destination folder is missing or inaccessible. Its saved history was kept."); }
    }
}
