using System.Buffers.Binary;

namespace AriaGui.Infrastructure.Downloads;

// Version-1 HTTP control format: https://aria2.github.io/manual/en/html/technical-notes.html
// This reader only measures aria2's durable bitmap. It never executes options
// or treats the bitmap as an origin validator or publisher checksum.
internal sealed record Aria2ControlCoverage(long TotalLength, long CoveredBytes, int PieceLength);

internal static class Aria2ControlCoverageReader
{
    internal static Aria2ControlCoverage Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length is < 34 or > 4 * 1024 * 1024) throw Invalid();
        var offset = 0;
        if (U16(data, ref offset) != 1 || U32(data, ref offset) != 0 || U32(data, ref offset) != 0) throw Invalid(); // no torrent info hash/extension
        var pieceLength = U32(data, ref offset); var total = U64(data, ref offset); var upload = U64(data, ref offset);
        if (pieceLength is < 16384 or > 64 * 1024 * 1024 || total is 0 or > 8UL * 1024 * 1024 * 1024 * 1024 || upload != 0)
            throw Invalid();
        var pieces = (total + pieceLength - 1) / pieceLength;
        var fieldLength = U32(data, ref offset);
        if (fieldLength != (pieces + 7) / 8 || fieldLength > 1024 * 1024) throw Invalid();
        Need(data, offset, (int)fieldLength); var field = data.Slice(offset, (int)fieldLength); offset += (int)fieldLength;
        long covered = 0;
        for (ulong index = 0; index < pieces; index++)
            if (Set(field, index)) covered += (long)Math.Min(pieceLength, total - index * pieceLength);
        if (TrailingSet(field, pieces)) throw Invalid();
        var inFlight = U32(data, ref offset);
        if (inFlight > pieces || inFlight > 1_000_000) throw Invalid();
        var seen = new HashSet<uint>();
        for (var entry = 0; entry < inFlight; entry++)
        {
            var index = U32(data, ref offset); var length = U32(data, ref offset); var chunksLength = U32(data, ref offset);
            if (index >= pieces || !seen.Add(index) || Set(field, index)) throw Invalid();
            var actualLength = (uint)Math.Min(pieceLength, total - index * pieceLength);
            var chunks = (actualLength + 16383) / 16384;
            if (length != actualLength || chunksLength != (chunks + 7) / 8 || chunksLength > 8192) throw Invalid();
            Need(data, offset, (int)chunksLength); var bits = data.Slice(offset, (int)chunksLength); offset += (int)chunksLength;
            if (TrailingSet(bits, chunks)) throw Invalid();
            for (uint chunk = 0; chunk < chunks; chunk++)
                if (Set(bits, chunk)) covered += Math.Min(16384, (int)(actualLength - chunk * 16384));
        }
        if (offset != data.Length || covered > (long)total) throw Invalid();
        return new((long)total, covered, (int)pieceLength);
    }
    private static void Need(ReadOnlySpan<byte> data, int offset, int count)
    { if (count < 0 || offset > data.Length - count) throw Invalid(); }
    private static ushort U16(ReadOnlySpan<byte> data, ref int offset)
    { Need(data, offset, 2); var value = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]); offset += 2; return value; }
    private static uint U32(ReadOnlySpan<byte> data, ref int offset)
    { Need(data, offset, 4); var value = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]); offset += 4; return value; }
    private static ulong U64(ReadOnlySpan<byte> data, ref int offset)
    { Need(data, offset, 8); var value = BinaryPrimitives.ReadUInt64BigEndian(data[offset..]); offset += 8; return value; }
    private static bool Set(ReadOnlySpan<byte> bits, ulong index) => (bits[(int)(index / 8)] & (0x80 >> (int)(index % 8))) != 0;
    private static bool TrailingSet(ReadOnlySpan<byte> bits, ulong count)
    {
        for (var index = count; index < (ulong)bits.Length * 8; index++) if (Set(bits, index)) return true;
        return false;
    }
    private static IOException Invalid() => new("Invalid aria2 HTTP control coverage.");
}
