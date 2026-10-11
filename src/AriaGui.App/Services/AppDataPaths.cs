using System.Runtime.InteropServices;
using AriaGui.Infrastructure.Aria2;

namespace AriaGui.App.Services;

internal static class AppDataPaths
{
    // Debug and explicitly opted-in verification builds accept a marked private profile.
    // Normal Release builds never inspect this marker.
    internal static string? VerificationDirectory
    {
        get
        {
#if DEBUG || ARIAGUI_VERIFICATION
            var marker = Path.Combine(AppContext.BaseDirectory, "r1-fixture-profile.txt");
            if (File.Exists(marker))
            {
                var path = Path.GetFullPath(File.ReadAllText(marker).Trim());
                if (!File.Exists(Path.Combine(path, "synthetic-r1-fixture")))
                    throw new InvalidOperationException("Verification profile is not explicitly marked synthetic.");
                return path;
            }
#endif
            return null;
        }
    }

    internal static string EngineExecutable => VerificationDirectory is { } fixture
        ? Path.Combine(fixture, "disabled-engine", "aria2c.exe")
        : Path.Combine(AppContext.BaseDirectory, "Engine", "aria2c.exe");
    // Explicit LocalState avoids relying on legacy AppData virtualization behavior.
    public static string RuntimeDirectory
    {
        get
        {
            if (VerificationDirectory is { } fixture) return fixture;
            uint length = 0;
            var result = GetCurrentPackageFullName(ref length, IntPtr.Zero);
            if (result == 15700) return UserRuntimeDirectory.DefaultPath; // APPMODEL_ERROR_NO_PACKAGE
            if (result != 122) throw new InvalidOperationException("Package identity could not be resolved.");
            return Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "Engine");
        }
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, IntPtr packageFullName);
}
