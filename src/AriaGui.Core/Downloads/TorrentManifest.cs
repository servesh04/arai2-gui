using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AriaGui.Core.Downloads;

public enum TransferKind { Http = 0, Torrent = 1 }
public enum TorrentStage { AwaitingSelection, Waiting, Checking, Downloading, Verifying, Publishing, Completed, Failed, Stopped }
public enum TorrentEngineRole { Payload = 1, Metadata = 2 }

public sealed record TorrentFile(int Index, string RelativePath, long Length);

public sealed record TorrentManifest(string InfoHash, string Name, IReadOnlyList<TorrentFile> Files,
    long TotalBytes, long PieceLength, IReadOnlyList<string> Trackers, bool IsPrivate)
{
    public TorrentSelection SelectFiles(IEnumerable<int> indices)
    {
        var selected = indices.Distinct().Order().ToArray();
        if (selected.Length == 0 || selected.Any(index => index < 1 || index > Files.Count))
            throw new ArgumentException("Select at least one valid torrent file.", nameof(indices));
        var bytes = selected.Aggregate(0L, (sum, index) => checked(sum + Files[index - 1].Length));
        return new TorrentSelection(Array.AsReadOnly(selected), bytes);
    }
}

public sealed record TorrentSelection(IReadOnlyList<int> FileIndices, long SelectedBytes);

/// <summary>Bounded, local-only v1 metainfo intake. No filesystem writes or network access.</summary>
public static class TorrentManifestParser
{
    public const int MaximumMetadataBytes = 4 * 1024 * 1024;
    private const int MaximumNodes = 100_000;
    private const int MaximumDepth = 16;
    private const int MaximumFiles = 4_096;
    private const int MaximumTrackers = 256;
    private const int MaximumPathBytes = 1_024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async Task<TorrentManifest> ParseAsync(Stream source, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(chunk, deadline.Token).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > MaximumMetadataBytes) throw Invalid("Torrent metadata is too large.");
            buffer.Write(chunk, 0, read);
        }
        var bytes = buffer.ToArray();
        return await Task.Run(() => Parse(bytes, deadline.Token), deadline.Token).ConfigureAwait(false);
    }

    public static TorrentManifest Parse(ReadOnlyMemory<byte> source, CancellationToken token = default)
    {
        if (source.Length is 0 or > MaximumMetadataBytes) throw Invalid("Torrent metadata is empty or too large.");
        var data = source.ToArray();
        var reader = new Reader(data, token);
        var root = reader.Read(0);
        if (reader.Position != data.Length || root.Dictionary is null) throw Invalid("Invalid torrent metadata.");
        var info = Required(root, "info");
        if (info.Dictionary is null) throw Invalid("Torrent info must be a dictionary.");
        if (info.Dictionary.ContainsKey("meta version")) throw Invalid("BitTorrent v2 and hybrid torrents are not supported yet.");
        if (info.Dictionary.ContainsKey("name.utf-8")) throw Invalid("Alternate torrent names are not supported yet.");
        var name = FileComponent(Required(info, "name"));
        var pieceLength = Number(Required(info, "piece length"));
        if (pieceLength is <= 0 or > 64 * 1024 * 1024) throw Invalid("Invalid torrent piece length.");
        var pieces = Bytes(Required(info, "pieces"));
        var hasLength = info.Dictionary.ContainsKey("length");
        var hasFiles = info.Dictionary.ContainsKey("files");
        if (hasLength == hasFiles) throw Invalid("Torrent must contain one file or a file list.");
        var files = hasLength ? SingleFile(info, name) : MultiFiles(info, name);
        long total;
        try { total = files.Aggregate(0L, (sum, file) => checked(sum + file.Length)); }
        catch (OverflowException) { throw Invalid("Torrent payload size is too large."); }
        if (total <= 0 || (total - 1) / pieceLength + 1 > MaximumMetadataBytes / 20
            || pieces.Length != ((total - 1) / pieceLength + 1) * 20)
            throw Invalid("Torrent piece hashes do not match the payload size.");
        var trackers = ReadTrackers(root);
        if (trackers.Count == 0) throw Invalid("A tracker is required while peer discovery is disabled.");
        var privateFlag = false;
        if (info.Dictionary.TryGetValue("private", out var flag))
        {
            var value = Number(flag);
            if (value is not (0 or 1)) throw Invalid("Invalid private torrent flag.");
            privateFlag = value == 1;
        }
        var hash = Convert.ToHexString(SHA1.HashData(data.AsSpan(info.Start, info.End - info.Start)));
        return new(hash, name, Array.AsReadOnly(files), total, pieceLength, Array.AsReadOnly(trackers.ToArray()), privateFlag);
    }

    private static TorrentFile[] SingleFile(Node info, string name)
    {
        var length = Number(Required(info, "length"));
        if (length < 0) throw Invalid("Torrent file length is negative.");
        return [new(1, name, length)];
    }

    private static TorrentFile[] MultiFiles(Node info, string root)
    {
        var entries = Required(info, "files").List;
        if (entries is null || entries.Count is 0 or > MaximumFiles) throw Invalid("Invalid torrent file count.");
        var files = new List<TorrentFile>(entries.Count);
        var usedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry.Dictionary is null) throw Invalid("Invalid torrent file entry.");
            if (entry.Dictionary.ContainsKey("path.utf-8")) throw Invalid("Alternate torrent paths are not supported yet.");
            var length = Number(Required(entry, "length"));
            if (length < 0) throw Invalid("Torrent file length is negative.");
            var path = Required(entry, "path").List;
            if (path is null || path.Count is 0 or > MaximumDepth) throw Invalid("Invalid torrent path depth.");
            var parts = new List<string> { root };
            foreach (var part in path) parts.Add(FileComponent(part));
            var relative = string.Join('\\', parts);
            if (Encoding.UTF8.GetByteCount(relative) > MaximumPathBytes || !usedFiles.Add(relative))
                throw Invalid("Torrent paths collide or are too long.");
            for (var i = 1; i < parts.Count; i++)
            {
                var directory = string.Join('\\', parts.Take(i));
                if (usedFiles.Contains(directory)) throw Invalid("Torrent file and directory paths collide.");
                if (directories.TryGetValue(directory, out var original) && original != directory)
                    throw Invalid("Torrent directory names differ only by case.");
                directories[directory] = directory;
            }
            if (directories.ContainsKey(relative)) throw Invalid("Torrent file and directory paths collide.");
            files.Add(new(files.Count + 1, relative, length));
        }
        return files.ToArray();
    }

    private static List<string> ReadTrackers(Node root)
    {
        var trackers = new List<string>();
        void Add(Node node)
        {
            if (trackers.Count >= MaximumTrackers) throw Invalid("Too many torrent trackers.");
            var address = Text(node, 2_048);
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Host.Length == 0
                || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
                || uri.Scheme is not ("http" or "https" or "udp")) throw Invalid("Invalid tracker URL.");
            if (!trackers.Contains(address, StringComparer.Ordinal)) trackers.Add(address);
        }
        if (root.Dictionary!.TryGetValue("announce", out var announce)) Add(announce);
        if (root.Dictionary.TryGetValue("announce-list", out var tiers))
        {
            if (tiers.List is null) throw Invalid("Invalid torrent tracker list.");
            foreach (var tier in tiers.List)
            {
                if (tier.List is null) throw Invalid("Invalid torrent tracker tier.");
                foreach (var tracker in tier.List) Add(tracker);
            }
        }
        return trackers;
    }

    private static string FileComponent(Node node)
    {
        var value = Text(node, WindowsFileNamePolicy.MaximumLength * 4);
        if (!WindowsFileNamePolicy.IsValid(value)) throw Invalid("Torrent contains an unsafe file name.");
        return value;
    }
    private static string Text(Node node, int maximumBytes)
    {
        var bytes = Bytes(node);
        if (bytes.Length > maximumBytes) throw Invalid("Torrent text is too long.");
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw Invalid("Torrent text is not UTF-8."); }
    }
    private static byte[] Bytes(Node node) => node.Bytes ?? throw Invalid("Expected torrent text.");
    private static long Number(Node node) => node.Number ?? throw Invalid("Expected torrent integer.");
    private static Node Required(Node node, string key) => node.Dictionary is not null && node.Dictionary.TryGetValue(key, out var value)
        ? value : throw Invalid($"Torrent metadata is missing {key}.");
    private static InvalidDataException Invalid(string message) => new(message);

    private sealed class Node
    {
        public int Start, End;
        public byte[]? Bytes;
        public long? Number;
        public List<Node>? List;
        public Dictionary<string, Node>? Dictionary;
    }

    private sealed class Reader(byte[] source, CancellationToken token)
    {
        private int _nodes;
        public int Position { get; private set; }
        public Node Read(int depth)
        {
            token.ThrowIfCancellationRequested();
            if (++_nodes > MaximumNodes || depth > MaximumDepth || Position >= source.Length)
                throw Invalid("Torrent metadata exceeds parser limits.");
            var node = new Node { Start = Position };
            var marker = source[Position];
            if (marker == 'i')
            {
                Position++;
                var start = Position;
                while (Position < source.Length && source[Position] != 'e') Position++;
                if (Position == source.Length) throw Invalid("Unterminated torrent integer.");
                var value = source.AsSpan(start, Position - start);
                if (value.Length == 0 || value[0] == '+' || value.SequenceEqual("-0"u8)
                    || (value.Length > 1 && value[0] == '0') || (value.Length > 2 && value[0] == '-' && value[1] == '0')
                    || !long.TryParse(Encoding.ASCII.GetString(value), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
                    throw Invalid("Invalid torrent integer.");
                node.Number = number;
                Position++;
            }
            else if (marker == (byte)'l' || marker == (byte)'d')
            {
                Position++;
                if (marker == 'l')
                {
                    node.List = [];
                    while (Position < source.Length && source[Position] != 'e') node.List.Add(Read(depth + 1));
                }
                else
                {
                    node.Dictionary = new(StringComparer.Ordinal);
                    byte[]? previous = null;
                    while (Position < source.Length && source[Position] != 'e')
                    {
                        var key = ReadBytes();
                        if (previous is not null && previous.AsSpan().SequenceCompareTo(key) >= 0)
                            throw Invalid("Torrent dictionary keys are unsorted or duplicated.");
                        previous = key;
                        string text;
                        try { text = StrictUtf8.GetString(key); }
                        catch (DecoderFallbackException) { throw Invalid("Invalid torrent dictionary key."); }
                        node.Dictionary.Add(text, Read(depth + 1));
                    }
                }
                if (Position == source.Length) throw Invalid("Unterminated torrent collection.");
                Position++;
            }
            else node.Bytes = ReadBytes();
            node.End = Position;
            return node;
        }
        private byte[] ReadBytes()
        {
            var start = Position;
            while (Position < source.Length && source[Position] >= '0' && source[Position] <= '9') Position++;
            if (start == Position || Position >= source.Length || source[Position] != ':'
                || (Position - start > 1 && source[start] == '0')
                || !int.TryParse(Encoding.ASCII.GetString(source, start, Position - start), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var length) || length > MaximumMetadataBytes)
                throw Invalid("Invalid torrent byte string length.");
            Position++;
            if (length > source.Length - Position) throw Invalid("Truncated torrent byte string.");
            var bytes = source.AsSpan(Position, length).ToArray();
            Position += length;
            return bytes;
        }
    }
}
