using System.Diagnostics;
using System.Text.Json.Nodes;
using AriaGui.Application.Downloads;
using AriaGui.Application.Engine;
using AriaGui.Core.Downloads;
using AriaGui.Infrastructure.Aria2;

namespace AriaGui.Infrastructure.Tests;

[Collection("Engine processes")]
public sealed class M3AdoptionTests
{
    [Theory]
    [InlineData("clean")]
    [InlineData("foreign-gid")]
    [InlineData("work")]
    [InlineData("route")]
    [InlineData("database")]
    [InlineData("active-clean")]
    [InlineData("active-route")]
    public async Task Parallel_survivor_adoption_requires_matching_durable_engine_transport_and_storage(string mutation)
    {
        using var runtime = new RealDownloadTests.DownloadTestRuntime();
        var active = mutation.StartsWith("active-", StringComparison.Ordinal);
        var clean = mutation is "clean" or "active-clean";
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "AriaGui.EngineFixture.exe");
        var start = new ProcessStartInfo(fixture) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var value in new[] { active ? "--m3-recovery-active-host" : "--m3-recovery-host", runtime.Options.RuntimeDirectory,
            runtime.Options.ExecutablePath, runtime.Options.RpcPort.ToString() }) start.ArgumentList.Add(value);
        Directory.CreateDirectory(runtime.Options.RuntimeDirectory);
        using var host = Process.Start(start) ?? throw new IOException("M3 owner fixture did not start.");
        int pid = 0;
        try
        {
            var line = await host.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(25));
            Assert.StartsWith("READY ", line);
            pid = int.Parse(line![6..]);
            host.Kill(entireProcessTree: false); // The owned aria2 process must survive the GUI host.
            await host.WaitForExitAsync();
            var entry = Assert.Single(await runtime.Repository().LoadAsync(default));
            Assert.Equal(4, entry.Attempt.HttpConnections);
            if (!active) Assert.Equal(DownloadStatus.Paused, entry.Progress.Status);
            var journalPath = Path.Combine(runtime.Options.RuntimeDirectory, "continuity", "transfers", entry.Attempt.EngineId + ".json");
            var journal = JsonNode.Parse(await File.ReadAllTextAsync(journalPath))!;
            var work = journal["Work"]!.GetValue<string>();
            var name = journal["WorkFileName"]?.GetValue<string>() ?? "m3-survivor.bin";
            var payload = active ? null : await File.ReadAllBytesAsync(Path.Combine(work, name));
            var control = active ? null : await File.ReadAllBytesAsync(Path.Combine(work, name + ".aria2"));
            if (mutation == "foreign-gid") journal["Gid"] = "fedcba0987654321";
            if (mutation == "work") journal["Work"] = work + "-foreign";
            if (mutation is "foreign-gid" or "work") await File.WriteAllTextAsync(journalPath, journal.ToJsonString());
            if (mutation is "route" or "active-route")
            {
                var routePath = Path.Combine(runtime.Options.RuntimeDirectory, "continuity", "route-" + entry.Attempt.Id.ToString("N") + ".json");
                var route = JsonNode.Parse(await File.ReadAllTextAsync(routePath))!;
                route["Source"] = "http://127.0.0.1/foreign.bin";
                await File.WriteAllTextAsync(routePath, route.ToJsonString());
            }
            if (mutation == "database")
            {
                using var db = runtime.Db();
                db.Attempts.Single(row => row.Id == entry.Attempt.Id).HttpConnections = 2;
                db.SaveChanges();
            }
            var options = runtime.Options with { UseContinuityGateway = true, ParallelHttpRanges = true,
                ContinuityTestOrigins = _ => new M3ProcessOrigin(active ? 10 : 2) };
            await using var manager = new Aria2ProcessManager(options, runtime.Repository());
            if (!clean)
            {
                await Assert.ThrowsAsync<EngineException>(() => manager.StartAsync(default));
                Assert.Null(manager.OwnedProcessId);
                Assert.False(Process.GetProcessById(pid).HasExited);
                Assert.False(File.Exists(entry.Job.Destination));
                if (!active)
                {
                    Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(work, name)));
                    Assert.Equal(control, await File.ReadAllBytesAsync(Path.Combine(work, name + ".aria2")));
                }
                return;
            }
            await manager.StartAsync(default);
            Assert.Equal(pid, manager.OwnedProcessId);
            var store = new DownloadRuntimeStore();
            var coordinator = runtime.Coordinator(new ManagedAria2Client(manager), store);
            await coordinator.InitializeAsync();
            var restored = Assert.Single(store.Current.Downloads);
            Assert.Equal(entry.Attempt, restored.Attempt);
            if (!active)
            {
                Assert.Equal(DownloadStatus.Paused, restored.Progress.Status);
                await coordinator.ResumeAsync(restored.Job.Id);
            }
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(45))
            {
                await coordinator.RefreshAsync();
                if (Assert.Single(store.Current.Downloads).Progress.Status is DownloadStatus.Completed or DownloadStatus.Failed) break;
                await Task.Delay(50);
            }
            var completed = Assert.Single(store.Current.Downloads);
            Assert.Equal(DownloadStatus.Completed, completed.Progress.Status);
            Assert.Equal(VerificationStatus.Verified, completed.Verification);
            Assert.Equal(M3ProcessOrigin.Bytes, await File.ReadAllBytesAsync(entry.Job.Destination));
        }
        finally
        {
            if (!host.HasExited) { host.Kill(entireProcessTree: false); await host.WaitForExitAsync(); }
            if (pid != 0)
            {
                try
                {
                    using var engine = Process.GetProcessById(pid);
                    if (!engine.HasExited) { engine.Kill(entireProcessTree: true); await engine.WaitForExitAsync(); }
                }
                catch (ArgumentException) { }
            }
        }
    }
}
