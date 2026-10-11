using System.Buffers.Binary;
using System.Text;

namespace AriaGui.Browser.Contracts;

public enum FrameTransport { NativeMessagingWindowsX64, PipeLittleEndian }
public enum FrameFailure { InvalidLength, InvalidUtf8, Truncated, Deadline, Closed }
public sealed class FrameException(FrameFailure failure) : IOException("Browser frame rejected.")
{ public FrameFailure Failure { get; } = failure; }

/// <summary>One instance per duplex connection. Caller owns and closes the stream after any fault.
/// Reads and writes are independently serialized. Deadlines include time queued for the gate.
/// Streams must honor asynchronous cancellation; no unbounded synchronous fallback is used.</summary>
public sealed class FrameConnection
{
    private readonly Stream _stream;
    private readonly TimeSpan _deadline;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _reader = new(1), _writer = new(1);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private int _faulted;
    public bool IsFaulted => Volatile.Read(ref _faulted) != 0;
    public FrameConnection(Stream stream, FrameTransport transport, TimeSpan? deadline = null, TimeProvider? timeProvider = null)
    {
        if (!Enum.IsDefined(transport)) throw new ArgumentOutOfRangeException(nameof(transport));
        if (transport == FrameTransport.NativeMessagingWindowsX64 && !BitConverter.IsLittleEndian)
            throw new PlatformNotSupportedException("Native framing requires little-endian Windows x64 convention.");
        _stream = stream; _deadline = deadline ?? ProtocolLimits.FrameDeadline; _timeProvider = timeProvider ?? TimeProvider.System;
        if (_deadline <= TimeSpan.Zero || _deadline > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(deadline));
    }
    public async ValueTask<byte[]?> ReadAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = new CancellationTokenSource(_deadline, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        var acquired = false;
        try
        {
            await _reader.WaitAsync(linked.Token); acquired = true; CheckOpen();
            var header = new byte[4];
            if (!await ExactAsync(header, true, linked.Token)) return null;
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (size is 0 or > ProtocolLimits.MaximumMessageBytes) throw new FrameException(FrameFailure.InvalidLength);
            var body = new byte[(int)size];
            await ExactAsync(body, false, linked.Token);
            ValidateUtf8(body); return body;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        { Fault(); throw new FrameException(FrameFailure.Deadline); }
        catch { if (acquired) Fault(); throw; }
        finally { if (acquired) _reader.Release(); }
    }
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
    {
        // Validate before sending any byte. Copy bounded input so a caller cannot mutate a queued frame.
        if (body.Length is < 1 or > ProtocolLimits.MaximumMessageBytes) throw new FrameException(FrameFailure.InvalidLength);
        var snapshot = body.ToArray();
        ValidateUtf8(snapshot);
        using var timeout = new CancellationTokenSource(_deadline, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        var acquired = false;
        try
        {
            await _writer.WaitAsync(linked.Token); acquired = true; CheckOpen();
            var header = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)snapshot.Length);
            await _stream.WriteAsync(header, linked.Token); await _stream.WriteAsync(snapshot, linked.Token);
            await _stream.FlushAsync(linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        { Fault(); throw new FrameException(FrameFailure.Deadline); }
        catch { if (acquired) Fault(); throw; }
        finally { if (acquired) _writer.Release(); }
    }
    private async ValueTask<bool> ExactAsync(Memory<byte> bytes, bool cleanEofAllowed, CancellationToken token)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            var count = await _stream.ReadAsync(bytes[offset..], token);
            if (count == 0)
            {
                if (offset == 0 && cleanEofAllowed) return false;
                throw new FrameException(FrameFailure.Truncated);
            }
            offset += count;
        }
        return true;
    }
    private static void ValidateUtf8(ReadOnlySpan<byte> bytes)
    {
        try { _ = Utf8.GetCharCount(bytes); }
        catch (DecoderFallbackException) { throw new FrameException(FrameFailure.InvalidUtf8); }
    }
    private void CheckOpen() { if (IsFaulted) throw new FrameException(FrameFailure.Closed); }
    private void Fault() => Interlocked.Exchange(ref _faulted, 1);
}
