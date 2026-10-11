using AriaGui.Core.Downloads;
using AriaGui.Application.Downloads;

namespace AriaGui.Infrastructure.Downloads;

public sealed partial class FileDownloadDestination : IDownloadDestination
{
    public Task<DownloadTarget> ValidateAsync(Uri source, string directory, string? fileName, CancellationToken cancellationToken) =>
        ValidateCoreAsync(source, directory, fileName, false, cancellationToken);

    public Task<DownloadTarget> ValidateRetryAsync(Uri source, string directory, string fileName, CancellationToken token) =>
        ValidateCoreAsync(source, directory, fileName, true, token);

    private static Task<DownloadTarget> ValidateCoreAsync(Uri source, string directory, string? fileName, bool retry, CancellationToken cancellationToken) => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            fileName = string.IsNullOrWhiteSpace(fileName) ? SuggestedName(source) : fileName.Trim();
            if (!Path.IsPathFullyQualified(directory) || directory.IndexOfAny(['\r', '\n']) >= 0)
                throw new ArgumentException("Choose an absolute destination folder.");
            if (!IsValidName(fileName)) throw new ArgumentException("Use a single valid Windows filename without folders or reserved device names.");
            try
            {
                directory = Path.GetFullPath(directory);
                Directory.CreateDirectory(directory);
                var target = new DownloadTarget(directory, fileName);
                if (Directory.Exists(target.FullPath) || Directory.Exists(target.FullPath + ".aria2"))
                    throw new DownloadRequestException("A directory occupies the download path. Keep it and choose a different destination.");
                if (!retry && (File.Exists(target.FullPath) || File.Exists(target.FullPath + ".aria2")))
                    throw new DownloadRequestException("That file or its partial download already exists. Choose another filename or folder.");
                if (retry && File.Exists(target.FullPath) && !File.Exists(target.FullPath + ".aria2"))
                    throw new DownloadRequestException("Retry cannot safely continue this file without its aria2 control file. Keep the file and add a new download with a different filename.");
                var probe = Path.Combine(directory, $".aria-gui-write-{Guid.NewGuid():N}.tmp");
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
                return target with { ContinueExisting = retry && File.Exists(target.FullPath + ".aria2") };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            { throw new DownloadRequestException("The destination folder is not writable. Choose another folder or check its permissions."); }
        }, cancellationToken);

    public static string SuggestedName(Uri source)
    {
        var name = Uri.UnescapeDataString(source.Segments.LastOrDefault() ?? "").Trim('/');
        return string.IsNullOrWhiteSpace(name) ? "download.bin" : name;
    }
    public static bool IsValidName(string name) => WindowsFileNamePolicy.IsValid(name);
}
