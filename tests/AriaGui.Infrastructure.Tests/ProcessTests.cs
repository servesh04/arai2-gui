using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using AriaGui.Application.Engine;
using AriaGui.Infrastructure.Aria2;

namespace AriaGui.Infrastructure.Tests;

[CollectionDefinition("Engine processes", DisableParallelization = true)]
public sealed class EngineProcessCollection;

[Collection("Engine processes")]
public sealed class ProcessTests
{
    [Fact]
    public async Task Real_engine_becomes_ready_reuses_owned_process_and_exits_gracefully()
    {
        using var runtime = new TestRuntime();
        await using var engine = new Aria2ProcessManager(runtime.Options);
        var info = await engine.StartAsync(CancellationToken.None);
        Assert.Equal("1.37.0", info.Version);
        var pid = engine.OwnedProcessId!.Value;
        Assert.Equal(info, await engine.CheckHealthAsync(CancellationToken.None));
        Assert.Equal(info, await engine.StartAsync(CancellationToken.None));
        Assert.Equal(pid, engine.OwnedProcessId);
        var configuration = await File.ReadAllTextAsync(Path.Combine(runtime.Path, "aria2.conf"));
        Assert.Contains("rpc-listen-all=false", configuration);
        var secret = configuration.Split('\n').Single(line => line.StartsWith("rpc-secret=")).Split('=')[1];
        Assert.Equal(64, secret.Length);
        Assert.DoesNotContain(secret, await File.ReadAllTextAsync(Path.Combine(runtime.Path, "engine.owner.json")));
        await engine.StopAsync(CancellationToken.None);
        Assert.Null(engine.OwnedProcessId);
        Assert.False(IsAlive(pid));
        Assert.False(File.Exists(Path.Combine(runtime.Path, "aria2.conf")));
        Assert.False(File.Exists(Path.Combine(runtime.Path, "engine.owner.json")));
        Assert.Single(Directory.GetFiles(System.IO.Path.Combine(runtime.Path, "sessions")));
        var log = await File.ReadAllTextAsync(Path.Combine(runtime.Path, "engine.log"));
        Assert.DoesNotContain(secret, log);
        Assert.Contains("exited with code 0", log);
    }

    [Fact]
    public async Task Real_engine_rejects_wrong_secret()
    {
        using var runtime = new TestRuntime();
        await using var engine = new Aria2ProcessManager(runtime.Options);
        await engine.StartAsync(CancellationToken.None);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var client = new Aria2RpcClient(http, new($"http://127.0.0.1:{runtime.Options.RpcPort}/jsonrpc"),
            "wrong-secret", TimeSpan.FromSeconds(2));
        var ex = await Assert.ThrowsAsync<EngineException>(() => client.GetInfoAsync());
        Assert.Equal(EngineFailure.Authentication, ex.Failure);
    }

    [Theory]
    [InlineData(true, EngineFailure.MissingBinary)]
    [InlineData(false, EngineFailure.InvalidBinary)]
    public async Task Missing_or_modified_binary_never_starts(bool missing, EngineFailure failure)
    {
        using var runtime = new TestRuntime();
        var options = missing ? runtime.Options with { ExecutablePath = Path.Combine(runtime.Path, "missing.exe") }
            : runtime.Options with { ExpectedSha256 = new string('0', 64) };
        await using var engine = new Aria2ProcessManager(options);
        var ex = await Assert.ThrowsAsync<EngineException>(() => engine.StartAsync(CancellationToken.None));
        Assert.Equal(failure, ex.Failure);
        Assert.Null(engine.OwnedProcessId);
    }

    [Fact]
    public async Task Occupied_port_is_not_contacted_or_terminated()
    {
        using var runtime = new TestRuntime();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Server.ExclusiveAddressUse = true;
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            await using var engine = new Aria2ProcessManager(runtime.Options with { RpcPort = port });
            var ex = await Assert.ThrowsAsync<EngineException>(() => engine.StartAsync(CancellationToken.None));
            Assert.Equal(EngineFailure.PortInUse, ex.Failure);
            Assert.False(listener.Pending());
            Assert.Null(engine.OwnedProcessId);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task Two_managers_cannot_own_the_same_runtime_folder()
    {
        using var runtime = new TestRuntime();
        await using var first = new Aria2ProcessManager(runtime.Options);
        await using var second = new Aria2ProcessManager(runtime.Options);
        await first.StartAsync(CancellationToken.None);
        var ex = await Assert.ThrowsAsync<EngineException>(() => second.StartAsync(CancellationToken.None));
        Assert.Equal(EngineFailure.AlreadyRunning, ex.Failure);
        Assert.NotNull(first.OwnedProcessId);
        Assert.Null(second.OwnedProcessId);
    }

    [Fact]
    public async Task Unexpected_exit_is_reported_and_restart_creates_a_new_session()
    {
        using var runtime = new TestRuntime();
        await using var engine = new Aria2ProcessManager(runtime.Options);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.UnexpectedExit += (_, _) => exited.TrySetResult();
        var previous = await engine.StartAsync(CancellationToken.None);
        using (var process = Process.GetProcessById(engine.OwnedProcessId!.Value))
        {
            process.Kill(); // Only the isolated engine this test started.
            await process.WaitForExitAsync();
        }
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var ex = await Assert.ThrowsAsync<EngineException>(() => engine.CheckHealthAsync(CancellationToken.None));
        Assert.Equal(EngineFailure.ProcessExited, ex.Failure);
        var restored = await engine.StartAsync(CancellationToken.None);
        Assert.NotEqual(previous.SessionId, restored.SessionId);
    }

    [Fact]
    public async Task Live_ownership_metadata_is_refused_without_touching_that_process()
    {
        using var runtime = new TestRuntime();
        UserRuntimeDirectory.EnsurePrivate(runtime.Path);
        using var unrelated = Process.GetCurrentProcess();
        var metadata = new { ProcessId = unrelated.Id, StartedAtUtc = unrelated.StartTime.ToUniversalTime(),
            ExecutablePath = "unrelated.exe", RpcPort = runtime.Options.RpcPort, SessionId = "unrelated",
            SessionFile = "", AppProcessId = unrelated.Id };
        var path = Path.Combine(runtime.Path, "engine.owner.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(metadata));
        await using var engine = new Aria2ProcessManager(runtime.Options);
        var ex = await Assert.ThrowsAsync<EngineException>(() => engine.StartAsync(CancellationToken.None));
        Assert.Equal(EngineFailure.AlreadyRunning, ex.Failure);
        Assert.False(unrelated.HasExited);
        Assert.Null(engine.OwnedProcessId);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Startup_deadline_cleans_up_only_its_unresponsive_child()
    {
        using var runtime = new TestRuntime();
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "AriaGui.EngineFixture.exe");
        var options = runtime.Options with { ExecutablePath = fixture, ExpectedSha256 = Hash(fixture),
            StartupTimeout = TimeSpan.FromMilliseconds(400), RequestTimeout = TimeSpan.FromMilliseconds(100),
            ShutdownTimeout = TimeSpan.FromMilliseconds(200) };
        await using var engine = new Aria2ProcessManager(options);
        var ex = await Assert.ThrowsAsync<EngineException>(() => engine.StartAsync(CancellationToken.None));
        Assert.Equal(EngineFailure.StartupTimeout, ex.Failure);
        Assert.Null(engine.OwnedProcessId);
        Assert.False(File.Exists(Path.Combine(runtime.Path, "aria2.conf")));
    }

    [Fact]
    public async Task Early_process_termination_is_distinguished_from_startup_timeout()
    {
        using var runtime = new TestRuntime();
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "AriaGui.EngineFixture.exe");
        Environment.SetEnvironmentVariable("ARIAGUI_FIXTURE_EXIT", "1");
        try
        {
            await using var engine = new Aria2ProcessManager(runtime.Options with
                { ExecutablePath = fixture, ExpectedSha256 = Hash(fixture), ShutdownTimeout = TimeSpan.FromMilliseconds(200) });
            var ex = await Assert.ThrowsAsync<EngineException>(() => engine.StartAsync(CancellationToken.None));
            Assert.Equal(EngineFailure.ProcessExited, ex.Failure);
            Assert.Null(engine.OwnedProcessId);
        }
        finally { Environment.SetEnvironmentVariable("ARIAGUI_FIXTURE_EXIT", null); }
    }

    [Fact]
    public async Task Caller_cancellation_cleans_up_partial_startup()
    {
        using var runtime = new TestRuntime();
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "AriaGui.EngineFixture.exe");
        await using var engine = new Aria2ProcessManager(runtime.Options with
        {
            ExecutablePath = fixture, ExpectedSha256 = Hash(fixture), ShutdownTimeout = TimeSpan.FromMilliseconds(200)
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.StartAsync(cancellation.Token));
        Assert.Null(engine.OwnedProcessId);
    }

    [Fact]
    public void App_instance_lease_is_unique_and_releases_on_disposal()
    {
        using var runtime = new TestRuntime();
        using (var first = new SingleInstanceLease(runtime.Path))
        using (var second = new SingleInstanceLease(runtime.Path))
        {
            Assert.True(first.IsPrimary);
            Assert.False(second.IsPrimary);
        }
        using var next = new SingleInstanceLease(runtime.Path);
        Assert.True(next.IsPrimary);
    }

    [Fact]
    public void Unavailable_instance_lock_is_a_storage_failure_not_a_duplicate_instance()
    {
        using var runtime = new TestRuntime();
        UserRuntimeDirectory.EnsurePrivate(runtime.Path);
        Directory.CreateDirectory(Path.Combine(runtime.Path, "app.lock"));
        var ex = Assert.Throws<EngineException>(() => new SingleInstanceLease(runtime.Path));
        Assert.Equal(EngineFailure.StorageUnavailable, ex.Failure);
    }

    [Fact]
    public void Runtime_directory_grants_access_only_to_current_user_and_system()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var runtime = new TestRuntime();
        UserRuntimeDirectory.EnsurePrivate(runtime.Path);
        var permissions = new DirectoryInfo(runtime.Path).GetAccessControl();
        Assert.True(permissions.AreAccessRulesProtected);
        var user = WindowsIdentity.GetCurrent().User!;
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var rules = permissions.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        foreach (var rule in rules)
            Assert.True(rule.IdentityReference.Equals(user) || rule.IdentityReference.Equals(system));
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static bool IsAlive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private sealed class TestRuntime : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AriaGUI.EngineTests", Guid.NewGuid().ToString("N"));
        public Aria2ProcessOptions Options { get; }
        public TestRuntime()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            Options = new(System.IO.Path.Combine(AppContext.BaseDirectory, "Engine", "aria2c.exe"), Path)
                { RpcPort = port, StartupTimeout = TimeSpan.FromSeconds(5) };
        }
        public void Dispose()
        {
            var expectedRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AriaGUI.EngineTests"));
            if (!System.IO.Path.GetFullPath(Path).StartsWith(expectedRoot + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected test cleanup path.");
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
