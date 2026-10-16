namespace AriaGui.Infrastructure.Aria2;

public sealed record Aria2ProcessOptions(string ExecutablePath, string RuntimeDirectory)
{
    public static Aria2ProcessOptions ForApplication(string executablePath, string runtimeDirectory) =>
        new(executablePath, runtimeDirectory) { UseContinuityGateway = true, ParallelHttpRanges = true };
    public const string BundledVersion = "1.37.0";
    public const string BundledSha256 = "BE2099C214F63A3CB4954B09A0BECD6E2E34660B886D4C898D260FEBFE9D70C2";
    public int RpcPort { get; init; } = 16800;
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(12);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public string ExpectedSha256 { get; init; } = BundledSha256;
    public bool UseContinuityGateway { get; init; }
    public bool EnableExpectedChecksumVerification { get; init; } = true;
    public bool ParallelHttpRanges { get; init; }
    internal Func<string, AriaGui.Infrastructure.Downloads.IContinuityOriginTransport>? ContinuityTestOrigins { get; init; }
}
