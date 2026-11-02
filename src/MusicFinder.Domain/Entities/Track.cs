namespace MusicFinder.Domain.Entities;

public sealed class Track
{
    /// <summary>
    /// Bump when the metadata reader extracts something new, so already-indexed files are re-read once.
    /// 1 = M2 (embedded artwork hash), 2 = M6 (codec, channels, bit depth, VBR, read errors, quality hints).
    /// </summary>
    public const int CurrentMetaVersion = 2;

    public long Id { get; set; }
    public long FolderId { get; set; }
    public string FullPath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Extension { get; set; } = "";
    public long FileSize { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public DateTime IndexedUtc { get; set; }
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? AlbumArtist { get; set; }
    public string? Album { get; set; }
    public string? GenreRaw { get; set; }
    public int? Year { get; set; }
    public int? TrackNumber { get; set; }
    public long? DurationMs { get; set; }
    public int? BitrateKbps { get; set; }
    public int? SampleRateHz { get; set; }
    public double? Bpm { get; set; }
    public string? MusicalKey { get; set; }
    public string? MusicBrainzRecordingId { get; set; }
    public string? ArtworkCacheKey { get; set; }
    public string? Codec { get; set; }
    public int? Channels { get; set; }
    public int? BitDepth { get; set; }
    /// <summary>Why the tags could not be read; null when they could.</summary>
    public string? ReadError { get; set; }
    /// <summary>Bit mask of <see cref="MusicFinder.Domain.Enums.IssueFlags"/>.</summary>
    public int IssueFlags { get; set; }
    public bool Missing { get; set; }
    public int MetaVersion { get; set; } = CurrentMetaVersion;
    /// <summary>Lowercased, diacritic-stripped, final-sigma-folded text used by FTS (see TextNormalizer).</summary>
    public string? NormalizedText { get; set; }
}

/// <summary>Lightweight projection used by the scanner to skip unchanged files.</summary>
public readonly record struct IndexedFile(long Id, string FullPath, long FileSize, DateTime ModifiedUtc, int MetaVersion);
