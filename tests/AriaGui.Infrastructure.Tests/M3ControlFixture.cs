using System.Buffers.Binary;

namespace AriaGui.Infrastructure.Tests;

internal static class M3ControlFixture
{
    internal static byte[] OnePiece(long total, bool covered)
    {
        var data = new byte[39];
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(0, 2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(10, 4), 16384);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(14, 8), (ulong)total);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(30, 4), 1);
        if (covered) data[34] = 0x80;
        return data;
    }
}
