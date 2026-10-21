using System.Security.Cryptography;
using System.Text;
using AriaGui.Core.Downloads;

namespace AriaGui.Core.Tests;

public sealed class TorrentManifestTests
{
    [Fact]
    public void SingleFileUsesRawInfoHashAndOneBasedSelection()
    {
        var info = Dict(("length", Integer(3)), ("name", Bytes("sample.bin")),
            ("piece length", Integer(4)), ("pieces", ByteString(new byte[20])));
        var manifest = TorrentManifestParser.Parse(Metainfo(info));

        Assert.Equal(Convert.ToHexString(SHA1.HashData(info)), manifest.InfoHash);
        Assert.Equal("sample.bin", manifest.Name);
        Assert.Equal(3, manifest.TotalBytes);
        Assert.Equal("sample.bin", Assert.Single(manifest.Files).RelativePath);
        Assert.Equal(3, manifest.SelectFiles([1]).SelectedBytes);
        Assert.Throws<ArgumentException>(() => manifest.SelectFiles([]));
        Assert.Throws<ArgumentException>(() => manifest.SelectFiles([2]));
    }

    [Fact]
    public void MultiFileSelectionUsesSelectedLengths()
    {
        var files = List(
            Dict(("length", Integer(2)), ("path", List(Bytes("folder"), Bytes("first.bin")))),
            Dict(("length", Integer(3)), ("path", List(Bytes("second.bin")))));
        var info = Dict(("files", files), ("name", Bytes("bundle")),
            ("piece length", Integer(4)), ("pieces", ByteString(new byte[40])));
        var manifest = TorrentManifestParser.Parse(Metainfo(info));

        Assert.Equal(5, manifest.TotalBytes);
        Assert.Equal(["bundle\\folder\\first.bin", "bundle\\second.bin"], manifest.Files.Select(file => file.RelativePath));
        Assert.Equal([2], manifest.SelectFiles([2, 2]).FileIndices);
        Assert.Equal(3, manifest.SelectFiles([2]).SelectedBytes);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("C:")]
    [InlineData("CON")]
    [InlineData("bad:name")]
    [InlineData("trailing.")]
    public void RejectsUnsafePathComponent(string name)
    {
        var files = List(Dict(("length", Integer(1)), ("path", List(Bytes(name)))));
        var info = Dict(("files", files), ("name", Bytes("bundle")),
            ("piece length", Integer(4)), ("pieces", ByteString(new byte[20])));
        Assert.Throws<InvalidDataException>(() => TorrentManifestParser.Parse(Metainfo(info)));
    }

    [Fact]
    public void RejectsCaseCollisionsAndFileDirectoryCollisions()
    {
        var caseFiles = List(File("Readme"), File("README"));
        var nestedFiles = List(File("folder"), File("folder", "child"));
        Assert.Throws<InvalidDataException>(() => TorrentManifestParser.Parse(Metainfo(MultiInfo(caseFiles))));
        Assert.Throws<InvalidDataException>(() => TorrentManifestParser.Parse(Metainfo(MultiInfo(nestedFiles))));
    }

    [Fact]
    public void RejectsUnsupportedOrInconsistentMetadata()
    {
        var v2 = Dict(("length", Integer(1)), ("meta version", Integer(2)), ("name", Bytes("a")),
            ("piece length", Integer(4)), ("pieces", ByteString(new byte[20])));
        var badPieces = Dict(("length", Integer(5)), ("name", Bytes("a")),
            ("piece length", Integer(4)), ("pieces", ByteString(new byte[20])));
        Assert.Throws<InvalidDataException>(() => TorrentManifestParser.Parse(Metainfo(v2)));
        Assert.Throws<InvalidDataException>(() => TorrentManifestParser.Parse(Metainfo(badPieces)));
        Assert.Throws<InvalidDataException>(() => TorrentManifestParser.Parse(Dict(("info", badPieces))));
        Assert.Throws<InvalidDataException>(() => TorrentManifestParser.Parse(new byte[TorrentManifestParser.MaximumMetadataBytes + 1]));
    }

    [Fact]
    public void RejectsOverflowingPayloadLengthAsInvalidMetadata()
    {
        var files = List(Dict(("length", Integer(long.MaxValue)), ("path", List(Bytes("first")))),
            Dict(("length", Integer(1)), ("path", List(Bytes("second")))));
        Assert.Throws<InvalidDataException>(() => TorrentManifestParser.Parse(Metainfo(MultiInfo(files))));
    }

    [Fact]
    public void RejectsAlternatePathKeysThatCouldChangeEngineFileNames()
    {
        var alternateName = Dict(("length", Integer(1)), ("name", Bytes("safe")),
            ("name.utf-8", Bytes("unsafe")), ("piece length", Integer(4)),
            ("pieces", ByteString(new byte[20])));
        var files = List(Dict(("length", Integer(1)), ("path", List(Bytes("safe"))),
            ("path.utf-8", List(Bytes("unsafe")))));
        Assert.Throws<InvalidDataException>(() => TorrentManifestParser.Parse(Metainfo(alternateName)));
        Assert.Throws<InvalidDataException>(() => TorrentManifestParser.Parse(Metainfo(MultiInfo(files))));
    }

    private static byte[] MultiInfo(byte[] files) => Dict(("files", files), ("name", Bytes("bundle")),
        ("piece length", Integer(4)), ("pieces", ByteString(new byte[20])));
    private static byte[] File(params string[] path) => Dict(("length", Integer(1)),
        ("path", List(path.Select(Bytes).ToArray())));
    private static byte[] Metainfo(byte[] info) => Dict(("announce", Bytes("http://127.0.0.1:53623/announce")), ("info", info));
    private static byte[] Bytes(string value) => ByteString(Encoding.UTF8.GetBytes(value));
    private static byte[] ByteString(byte[] value) => [.. Encoding.ASCII.GetBytes(value.Length + ":"), .. value];
    private static byte[] Integer(long value) => Encoding.ASCII.GetBytes($"i{value}e");
    private static byte[] List(params byte[][] values) => [(byte)'l', .. values.SelectMany(value => value), (byte)'e'];
    private static byte[] Dict(params (string Key, byte[] Value)[] entries) =>
        [(byte)'d', .. entries.OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .SelectMany(entry => Bytes(entry.Key).Concat(entry.Value)), (byte)'e'];
}
