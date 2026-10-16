using System.Buffers.Binary;
using AriaGui.Infrastructure.Downloads;

namespace AriaGui.Infrastructure.Tests;

public sealed class Aria2ControlCoverageTests
{
    [Fact]
    public void One_piece_bitmap_reports_exact_coverage()
    {
        Assert.Equal(0, Aria2ControlCoverageReader.Parse(M3ControlFixture.OnePiece(4096, false)).CoveredBytes);
        Assert.Equal(4096, Aria2ControlCoverageReader.Parse(M3ControlFixture.OnePiece(4096, true)).CoveredBytes);
    }
    [Fact]
    public void Inflight_chunk_coverage_is_not_mistaken_for_whole_piece()
    {
        var data = new byte[39 + 12 + 1];
        M3ControlFixture.OnePiece(32768, false).CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(35, 4), 1); // one in-flight piece
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(39, 4), 0); // index
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(43, 4), 16384); // piece length
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(47, 4), 1); // chunk bitfield byte
        data[51] = 0x80;
        Assert.Equal(16384, Aria2ControlCoverageReader.Parse(data).CoveredBytes);
    }
    [Theory]
    [InlineData("old-version")]
    [InlineData("torrent-hash")]
    [InlineData("wrong-field-size")]
    [InlineData("trailing-bit")]
    [InlineData("oversized-inflight")]
    [InlineData("truncated")]
    public void Unsupported_or_corrupt_control_data_fails_closed(string mutation)
    {
        var data = M3ControlFixture.OnePiece(4096, false);
        switch (mutation)
        {
            case "old-version": data[1] = 0; break;
            case "torrent-hash": data[5] = 1; break;
            case "wrong-field-size": data[33] = 2; break;
            case "trailing-bit": data[34] = 0x40; break;
            case "oversized-inflight": data[38] = 2; break;
            case "truncated": data = data[..^1]; break;
        }
        Assert.Throws<IOException>(() => Aria2ControlCoverageReader.Parse(data));
    }
}
