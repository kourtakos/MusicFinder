namespace MusicFinder.Domain.Models;

/// <summary>The observed facts about one track that Find Similar compares. Anything missing stays missing: nothing is guessed.</summary>
public sealed record SimilarSignals(
    long TrackId, string Title, string? Artist, int? Year, double? Bpm, string? MusicalKey,
    IReadOnlyList<long> GenreIds, IReadOnlyList<long> TagIds);

public sealed record SimilarWeights(int Genre = 35, int Bpm = 30, int Key = 25, int Tags = 10);

public sealed record SimilarTracksOptions
{
    /// <summary>Tracks within this many BPM of the seed count as a tempo match (the score falls linearly to 0 at the window's edge).</summary>
    public double BpmWindow { get; init; } = 8;
    /// <summary>Also compare against double and half the seed's tempo (70 and 140 mix well). Such matches are labelled as such.</summary>
    public bool AllowHalfDoubleTempo { get; init; } = true;
    /// <summary>Leave out tracks played (even only possibly) since this time.</summary>
    public DateTime? ExcludePlayedSinceUtc { get; init; }
    /// <summary>Leave out tracks already played in this DJ session.</summary>
    public long? ExcludeSessionId { get; init; }
    public int MaxResults { get; init; } = 100;
    public SimilarWeights Weights { get; init; } = new();
    /// <summary>A result needs signals worth at least this much weight (of 100) to be shown; otherwise the score would claim more than the data supports.</summary>
    public int MinAvailableWeight { get; init; } = 55;
}

/// <param name="AvailableWeight">How much of the 100 points the score could be based on (signals both tracks have).</param>
/// <param name="Reasons">Why it matched, in plain words: "same genre (Disco)", "BPM +3", "compatible key (8A → 9A)".</param>
public sealed record SimilarTrack(
    SearchResultRow Row, int Score, int AvailableWeight, IReadOnlyList<string> Reasons, bool UsedHalfDoubleTempo);

public enum SimilarityStatus
{
    Ok,
    /// <summary>The track has too few of genre, BPM and key to compare by.</summary>
    InsufficientData,
    SeedNotFound
}

public sealed record SimilarTracksResult(SearchResultRow? Seed, SimilarityStatus Status, string? Message, IReadOnlyList<SimilarTrack> Items);
