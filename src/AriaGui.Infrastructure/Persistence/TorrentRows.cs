using AriaGui.Core.Downloads;

namespace AriaGui.Infrastructure.Persistence;

// Torrent persistence stays separate; the shared transfer collection projects these rows for browsing.
public sealed class TorrentSpecRow
{
    public Guid JobId { get; set; }
    public string InfoHash { get; set; } = "";
    public string Name { get; set; } = "";
    public byte[] Metainfo { get; set; } = [];
    public long TotalBytes { get; set; }
    public long PieceLength { get; set; }
    public bool IsPrivate { get; set; }
    public string PayloadRoot { get; set; } = "";
    public string DestinationRoot { get; set; } = "";
    public string DestinationKey { get; set; } = "";
    public long SelectedBytes { get; set; }
    public TorrentStage Stage { get; set; }
    public long CreatedAtUtcTicks { get; set; }
    public long UpdatedAtUtcTicks { get; set; }
}

public sealed class TorrentFileRow
{
    public Guid JobId { get; set; }
    public int FileIndex { get; set; }
    public string RelativePath { get; set; } = "";
    public long Length { get; set; }
    public bool Selected { get; set; }
    public long PublishedBytes { get; set; }
    public bool Published { get; set; }
}

public sealed class TorrentEngineRow
{
    public Guid JobId { get; set; }
    public TorrentEngineRole Role { get; set; }
    public string Generation { get; set; } = "";
    public string EngineId { get; set; } = "";
    public string InfoHash { get; set; } = "";
    public long RegisteredAtUtcTicks { get; set; }
}
