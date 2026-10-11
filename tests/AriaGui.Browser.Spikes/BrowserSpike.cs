using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace AriaGui.Browser.Spikes;

// Explicit opt-in development probe. No production keys or browser profiles are used.
internal static class BrowserSpike
{
    internal static async Task<object> ProbeAsync(string browserPath, string outputPath)
    {
        var root = Path.GetFullPath(outputPath);
        if (!File.Exists(browserPath) || (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()))
            throw new ArgumentException("Existing browser and new empty output directory required.");
        Directory.CreateDirectory(root);
        var name = "com.ariagui.wp0_" + Guid.NewGuid().ToString("N");
        var manifest = Path.Combine(root, "native-host.json");
        var extension = Directory.CreateDirectory(Path.Combine(root, "extension")).FullName;
        var template = Path.Combine(AppContext.BaseDirectory, "fixtures", "chromium");
        File.Copy(Path.Combine(template, "manifest.json"), Path.Combine(extension, "manifest.json"));
        File.WriteAllText(Path.Combine(extension, "background.js"), File.ReadAllText(Path.Combine(template, "background.js"))
            .Replace("com.ariagui.wp0_spike", name, StringComparison.Ordinal)
            .Replace("chrome.storage.local.set({ spikeResult: reply.result?.code, requestId: reply.requestId });",
                "console.info('WP0_NATIVE_HELLO:' + reply.result?.code); chrome.storage.local.set({ spikeResult: reply.result?.code, requestId: reply.requestId });", StringComparison.Ordinal));
        File.WriteAllText(manifest, JsonSerializer.Serialize(new
        {
            name,
            description = "Temporary Package 0 synthetic hello",
            path = Environment.ProcessPath,
            type = "stdio",
            allowed_origins = new[] { $"chrome-extension://{NativeSpike.ExtensionId}/" }
        }));
        string[] keys = [@"Software\Chromium\NativeMessagingHosts\" + name, @"Software\Google\Chrome\NativeMessagingHosts\" + name];
        foreach (var key in keys)
        {
            using var existing = Registry.CurrentUser.OpenSubKey(key);
            if (existing is not null) throw new InvalidOperationException("Development host key already exists.");
        }
        var created = new List<string>(); Process? browser = null;
        var hello = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lines = new List<string>();
        var cleaned = true;
        try
        {
            foreach (var path in keys)
            {
                using var key = Registry.CurrentUser.CreateSubKey(path); key.SetValue("", manifest, RegistryValueKind.String); created.Add(path);
                Program.Check((string?)key.GetValue("") == manifest, "Development registration readback");
            }
            var start = new ProcessStartInfo(Path.GetFullPath(browserPath))
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            foreach (var argument in new[] { "--headless=new", "--no-first-run", "--no-default-browser-check", "--disable-sync", "--disable-background-networking",
                "--disable-component-update", "--enable-logging=stderr", "--log-level=0", "--user-data-dir=" + Path.Combine(root, "profile"),
                "--load-extension=" + extension, "about:blank" }) start.ArgumentList.Add(argument);
            browser = new Process { StartInfo = start };
            browser.ErrorDataReceived += (_, args) =>
            {
                if (args.Data?.Contains("WP0_NATIVE_HELLO:", StringComparison.Ordinal) == true)
                {
                    lock (lines) lines.Add(args.Data);
                    if (args.Data.Contains("WP0_NATIVE_HELLO:spike_ready", StringComparison.Ordinal)) hello.TrySetResult();
                }
            };
            Program.Check(browser.Start(), "Synthetic browser launch"); browser.BeginErrorReadLine(); browser.BeginOutputReadLine();
            await hello.Task.WaitAsync(TimeSpan.FromSeconds(25));
        }
        finally
        {
            try
            {
                if (browser is not null)
                {
                    if (!browser.HasExited) { browser.Kill(entireProcessTree: true); await browser.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                    browser.Dispose();
                }
            }
            finally
            {
                foreach (var path in created)
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(path))
                    {
                        if (key is null) continue;
                        if (key.ValueCount != 1 || key.SubKeyCount != 0 || (string?)key.GetValue("") != manifest) { cleaned = false; continue; }
                    }
                    Registry.CurrentUser.DeleteSubKey(path, throwOnMissingSubKey: false);
                }

                foreach (var path in created) { using var check = Registry.CurrentUser.OpenSubKey(path); if (check is not null) cleaned = false; }
                File.WriteAllText(Path.Combine(root, "browser-evidence.json"), JsonSerializer.Serialize(new
                {
                    receivedHello = hello.Task.IsCompletedSuccessfully,
                    temporaryKeysRemoved = cleaned,
                    extensionId = NativeSpike.ExtensionId,
                    browserVersion = FileVersionInfo.GetVersionInfo(browserPath).FileVersion,
                    browserBrand = "cached Chromium",
                    personalProfileUsed = false,
                    machineWideRegistryModified = false,
                    consoleMarkers = lines.ToArray()
                },
                    new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        Program.Check(cleaned, "Registration rollback");
        return new { receivedHello = true, temporaryKeysRemoved = cleaned, artifactDirectory = root };
    }
}
