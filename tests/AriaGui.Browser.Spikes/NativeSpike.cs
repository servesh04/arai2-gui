using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AriaGui.Browser.Spikes;

// A hello-only executable spike, not the production parser/relay or an intake API.
internal static class NativeSpike
{
    internal const int Limit = 65536;
    internal static string ExtensionId
    {
        get
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "chromium", "manifest.json")));
            var hash = SHA256.HashData(Convert.FromBase64String(fixture.RootElement.GetProperty("key").GetString()!));
            return string.Concat(hash.Take(16).SelectMany(b => new[] { (char)('a' + (b >> 4)), (char)('a' + (b & 15)) }));
        }
    }
    internal static Process Start(params string[] arguments)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info)!;
    }
    internal static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        var first = await stream.ReadAsync(header.AsMemory(0, 1), token);
        if (first == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), token);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is 0 or > Limit) throw new InvalidDataException("Invalid frame size.");
        var body = new byte[(int)length];
        await stream.ReadExactlyAsync(body, token); return body;
    }
    internal static async Task WriteAsync(Stream stream, byte[] body, CancellationToken token)
    {
        if (body.Length is 0 or > Limit) throw new InvalidDataException();
        var header = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)body.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(body, token); await stream.FlushAsync(token);
    }
    internal static async Task<int> HostAsync(string[] args)
    {
        if (args.Length is < 1 or > 2 || args[0] != $"chrome-extension://{ExtensionId}/"
            || (args.Length == 2 && (!args[1].StartsWith("--parent-window=", StringComparison.Ordinal)
                || !ulong.TryParse(args[1][16..], out _)))) return 3;
        var input = Console.OpenStandardInput(); var output = Console.OpenStandardOutput();
        while (true)
        {
            using var idle = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var body = await ReadAsync(input, idle.Token); if (body is null) return 0;
            using var json = JsonDocument.Parse(new UTF8Encoding(false, true).GetString(body), new JsonDocumentOptions { MaxDepth = 8 });
            var request = json.RootElement;
            if (request.GetProperty("action").GetString() != "hello" || request.GetProperty("protocol").GetProperty("major").GetInt32() != 1
                || !Guid.TryParseExact(request.GetProperty("requestId").GetString(), "D", out var id)) return 4;
            await WriteAsync(output, JsonSerializer.SerializeToUtf8Bytes(new
            {
                protocol = new { major = 1, minor = 0 },
                requestId = id,
                result = new { code = "spike_ready", capabilities = Array.Empty<string>(), maxMessageBytes = Limit }
            }), idle.Token);
        }
    }
    internal static async Task<object> ProbeAsync(string root)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var requestId = Guid.NewGuid();
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            protocol = new { major = 1, minor = 0 },
            requestId,
            action = "hello",
            payload = new { extensionVersion = "0.0.0", installationId = Guid.NewGuid(), synthetic = "non-ASCII-λ" }
        },
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Program.Check(body.Length > Encoding.UTF8.GetString(body).Length, "Length is UTF-8 bytes, not UTF-16 characters");
        using var host = Start($"chrome-extension://{ExtensionId}/", "--parent-window=0");
        var header = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)body.Length);
        // Deliberately split the frame to exercise exact-read loops.
        foreach (var b in header) { await host.StandardInput.BaseStream.WriteAsync(new[] { b }, deadline.Token); await host.StandardInput.BaseStream.FlushAsync(deadline.Token); }
        await host.StandardInput.BaseStream.WriteAsync(body.AsMemory(0, 7), deadline.Token);
        await host.StandardInput.BaseStream.WriteAsync(body.AsMemory(7), deadline.Token);
        await host.StandardInput.BaseStream.FlushAsync(deadline.Token);
        var reply = await ReadAsync(host.StandardOutput.BaseStream, deadline.Token);
        using var document = JsonDocument.Parse(reply!);
        Program.Check(document.RootElement.GetProperty("requestId").GetGuid() == requestId, "Correlated reply");
        Program.Check(!Encoding.UTF8.GetString(reply!).Contains("synthetic", StringComparison.Ordinal), "No input echo");
        host.StandardInput.Close(); await host.WaitForExitAsync(deadline.Token);
        Program.Check(host.ExitCode == 0 && (await host.StandardOutput.ReadToEndAsync(deadline.Token)).Length == 0, "No stdout contamination");
        Program.Check((await host.StandardError.ReadToEndAsync(deadline.Token)).Length == 0, "No normal stderr log");
        using var oversize = Start($"chrome-extension://{ExtensionId}/");
        BinaryPrimitives.WriteUInt32LittleEndian(header, Limit + 1);
        await oversize.StandardInput.BaseStream.WriteAsync(header, deadline.Token); oversize.StandardInput.Close();
        await oversize.WaitForExitAsync(deadline.Token);
        Program.Check(oversize.ExitCode != 0 && (await oversize.StandardOutput.ReadToEndAsync(deadline.Token)).Length == 0, "Oversize closed without reply");
        using var wrongCaller = Start("chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/");
        wrongCaller.StandardInput.Close(); await wrongCaller.WaitForExitAsync(deadline.Token);
        Program.Check(wrongCaller.ExitCode == 3 && (await wrongCaller.StandardOutput.ReadToEndAsync(deadline.Token)).Length == 0, "Caller mismatch");
        var manifest = new
        {
            name = "com.ariagui.wp0_spike",
            description = "Isolated hello-only Package 0 spike",
            path = Environment.ProcessPath,
            type = "stdio",
            allowed_origins = new[] { $"chrome-extension://{ExtensionId}/" }
        };
        await File.WriteAllTextAsync(Path.Combine(root, "chromium-native-host.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        await File.WriteAllBytesAsync(Path.Combine(root, "hello-response.json"), reply!);
        return new { checks = 7, extensionId = ExtensionId, actualBrowserLaunch = "unverified", registryModified = false };
    }
}
