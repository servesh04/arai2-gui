using System.Buffers.Binary;
using System.Text;
using AriaGui.Browser.Contracts;

namespace AriaGui.Browser.Contracts.Tests;

public sealed class FrameTests
{
    [Theory]
    [InlineData(FrameTransport.NativeMessagingWindowsX64)]
    [InlineData(FrameTransport.PipeLittleEndian)]
    public async Task C01PartialHeaderBodyAndConcatenation(FrameTransport transport)
    {
        var body = Encoding.UTF8.GetBytes("{\"name\":\"資料 λ.zip\"}");
        using var stream = new ChunkStream(); var writer = new FrameConnection(stream, transport);
        await writer.WriteAsync(body); await writer.WriteAsync(body);
        Assert.Equal(body.Length, BinaryPrimitives.ReadInt32LittleEndian(stream.ToArray()));
        stream.Position = 0; var reader = new FrameConnection(stream, transport);
        Assert.Equal(body, await reader.ReadAsync()); Assert.Equal(body, await reader.ReadAsync());
        Assert.Null(await reader.ReadAsync()); Assert.False(reader.IsFaulted);
    }
    [Theory]
    [InlineData(0u)]
    [InlineData(65537u)]
    [InlineData(uint.MaxValue)]
    public async Task C02BadLengthRejectedBeforeBodyRead(uint length)
    {
        var header = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(header, length);
        using var stream = new ChunkStream(header); var connection = new FrameConnection(stream, FrameTransport.PipeLittleEndian);
        var error = await Assert.ThrowsAsync<FrameException>(async () => await connection.ReadAsync());
        Assert.Equal(FrameFailure.InvalidLength, error.Failure); Assert.Equal(4, stream.Position); Assert.True(connection.IsFaulted);
        Assert.Equal(FrameFailure.Closed, (await Assert.ThrowsAsync<FrameException>(async () => await connection.ReadAsync())).Failure);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task C02PartialEofFaultsConnection(int count)
    {
        byte[] frame = [3, 0, 0, 0, (byte)'a', (byte)'b', (byte)'c'];
        using var stream = new ChunkStream(frame[..count]); var connection = new FrameConnection(stream, FrameTransport.PipeLittleEndian);
        var error = await Assert.ThrowsAsync<FrameException>(async () => await connection.ReadAsync());
        Assert.Equal(FrameFailure.Truncated, error.Failure); Assert.True(connection.IsFaulted);
        Assert.DoesNotContain("abc", error.Message);
    }
    [Fact]
    public async Task C02InvalidUtf8AndOutboundSize()
    {
        using var stream = new ChunkStream([2, 0, 0, 0, 0xc0, 0xaf]); var connection = new FrameConnection(stream, FrameTransport.PipeLittleEndian);
        Assert.Equal(FrameFailure.InvalidUtf8, (await Assert.ThrowsAsync<FrameException>(async () => await connection.ReadAsync())).Failure);
        using var output = new MemoryStream(); var writer = new FrameConnection(output, FrameTransport.PipeLittleEndian);
        await Assert.ThrowsAsync<FrameException>(async () => await writer.WriteAsync(new byte[65537]));
        await Assert.ThrowsAsync<FrameException>(async () => await writer.WriteAsync(new byte[] { 0xff }));
        Assert.Equal(0, output.Length); Assert.False(writer.IsFaulted);
    }
    [Fact]
    public async Task C01ExactMaximumBodyAccepted()
    {
        using var stream = new MemoryStream(); var connection = new FrameConnection(stream, FrameTransport.PipeLittleEndian);
        var body = Enumerable.Repeat((byte)' ', 65536).ToArray(); await connection.WriteAsync(body); stream.Position = 0;
        Assert.Equal(body, await connection.ReadAsync());
    }
    [Fact]
    public async Task ConcurrentWritersCannotInterleaveHeadersAndBodies()
    {
        using var stream = new ChunkStream(); var connection = new FrameConnection(stream, FrameTransport.PipeLittleEndian);
        var bodies = Enumerable.Range(0, 20).Select(index => Encoding.UTF8.GetBytes($"{{\"request\":{index}}}")).ToArray();
        await Task.WhenAll(bodies.Select(body => connection.WriteAsync(body).AsTask()));
        stream.Position = 0; var received = new List<string>();
        while (await connection.ReadAsync() is { } body) received.Add(Encoding.UTF8.GetString(body));
        Assert.Equal(bodies.Select(Encoding.UTF8.GetString).Order(), received.Order());
    }
    [Fact]
    public async Task InjectedReadDeadlineFaultsWithoutSleeping()
    {
        var clock = new ManualClock(); using var stream = new BlockingStream(); var connection = new FrameConnection(stream, FrameTransport.PipeLittleEndian, timeProvider: clock);
        var read = connection.ReadAsync().AsTask(); await stream.Entered.Task;
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(FrameFailure.Deadline, (await Assert.ThrowsAsync<FrameException>(() => read)).Failure); Assert.True(connection.IsFaulted);
    }
    [Fact]
    public async Task InjectedWriteDeadlineFaultsWithoutSleeping()
    {
        var clock = new ManualClock(); using var stream = new BlockingStream(); var connection = new FrameConnection(stream, FrameTransport.PipeLittleEndian, timeProvider: clock);
        var write = connection.WriteAsync("{}"u8.ToArray()).AsTask(); await stream.Entered.Task;
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(FrameFailure.Deadline, (await Assert.ThrowsAsync<FrameException>(() => write)).Failure); Assert.True(connection.IsFaulted);
    }
    [Fact]
    public async Task CallerCancellationDuringReadFaultsConnection()
    {
        using var stream = new BlockingStream(); using var cancel = new CancellationTokenSource();
        var connection = new FrameConnection(stream, FrameTransport.PipeLittleEndian);
        var read = connection.ReadAsync(cancel.Token).AsTask(); await stream.Entered.Task; cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read); Assert.True(connection.IsFaulted);
    }
    private sealed class ChunkStream : MemoryStream
    {
        internal ChunkStream() { }
        internal ChunkStream(byte[] data) : base(data) { }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { foreach (var value in buffer.ToArray()) { cancellationToken.ThrowIfCancellationRequested(); WriteByte(value); await Task.Yield(); } }
    }
    private sealed class BlockingStream : Stream
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { Entered.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { Entered.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
    }
    private sealed class ManualClock : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        internal void Advance(TimeSpan amount) { foreach (var timer in _timers.ToArray()) timer.Advance(amount); }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new ManualTimer(callback, state, dueTime); _timers.Add(timer); return timer; }
        private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
        {
            private bool _disposed;
            internal void Advance(TimeSpan amount) { if (!_disposed && dueTime != Timeout.InfiniteTimeSpan) { dueTime -= amount; if (dueTime <= TimeSpan.Zero) { _disposed = true; callback(state); } } }
            public bool Change(TimeSpan due, TimeSpan period) { dueTime = due; return !_disposed; }
            public void Dispose() => _disposed = true; public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
