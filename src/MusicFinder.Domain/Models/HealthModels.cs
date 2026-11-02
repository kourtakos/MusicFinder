namespace MusicFinder.Domain.Models;

/// <summary>One kind of finding in Library Health. Each is counted from stored data only; nothing is re-read from disk.</summary>
public enum HealthIssueKind
{
    // Files
    MissingFiles,
    NotChecked,
    // Tags
    MissingTitle,
    MissingArtist,
    MissingAlbum,
    MissingGenre,
    MissingYear,
    MissingBpm,
    MissingKey,
    MissingArtwork,
    Mojibake,
    // Quality
    Unreadable,
    LowBitrate,
    OddSampleRate,
    CodecMismatch,
    SizeMismatch,
    ZeroDuration,
    // Duplicates
    ExactDuplicates
}

public sealed record HealthSummary(
    int TotalTracks,
    IReadOnlyDictionary<HealthIssueKind, int> Counts,
    IReadOnlyList<(string Extension, int Count)> Unsupported)
{
    public int CountOf(HealthIssueKind kind) => Counts.TryGetValue(kind, out var n) ? n : 0;
    public int UnsupportedTotal => Unsupported.Sum(u => u.Count);
}
