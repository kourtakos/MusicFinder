namespace MusicFinder.Domain.Models;

public sealed record ScanProgress(
    string Phase, int FoldersFound, int FilesFound, int FilesProcessed, int Errors, string? CurrentPath);

public sealed record TrackMetadata
{
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? AlbumArtist { get; init; }
    public string? Album { get; init; }
    public string? GenreRaw { get; init; }
    public int? Year { get; init; }
    public int? TrackNumber { get; init; }
    public long? DurationMs { get; init; }
    public int? BitrateKbps { get; init; }
    public int? SampleRateHz { get; init; }
    public double? Bpm { get; init; }
    public string? MusicalKey { get; init; }
    public string? MusicBrainzRecordingId { get; init; }
    /// <summary>Lowercase hex SHA-1 of the embedded front cover; the content-addressed artwork cache key. Null when none.</summary>
    public string? ArtworkHash { get; init; }
    /// <summary>First codec the container reports ("MPEG Version 1 Audio, Layer 3", "FLAC", ...). Null when unknown.</summary>
    public string? Codec { get; init; }
    public int? Channels { get; init; }
    public int? BitDepth { get; init; }
}
