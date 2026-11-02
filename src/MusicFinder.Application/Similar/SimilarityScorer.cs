using System.Globalization;
using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Similar;

/// <summary>The outcome of comparing one candidate with the seed. <c>null</c> from <see cref="SimilarityScorer.Score"/> means "not enough to compare by".</summary>
public sealed record SimilarityScore(
    int Score, int AvailableWeight, IReadOnlyList<string> Reasons, bool UsedHalfDoubleTempo, double? BpmDifference, bool SameArtist);

/// <summary>
/// Deterministic, offline "metadata compatibility" between two tracks, 0-100. It uses only what is stored: genre, BPM, key and DJ tags.
/// A signal counts only when BOTH tracks have it, and the weights of the signals that count are re-normalized, so a missing key never
/// lowers a score or is invented. This is NOT acoustic similarity and says nothing about energy or mood.
/// </summary>
public sealed class SimilarityScorer
{
    private readonly SimilarTracksOptions _options;
    private readonly GenreRelations _genres;
    private readonly IReadOnlyDictionary<long, string> _tagNames;

    public SimilarityScorer(SimilarTracksOptions options, GenreRelations genres, IReadOnlyDictionary<long, string> tagNames)
    {
        _options = options;
        _genres = genres;
        _tagNames = tagNames;
    }

    /// <summary>How many of genre, BPM and key the track has. Find Similar needs at least two to say anything.</summary>
    public static int SignalCount(SimilarSignals t) =>
        (t.GenreIds.Count > 0 ? 1 : 0) + (t.Bpm is > 0 ? 1 : 0) + (CamelotKey.TryParse(t.MusicalKey, out _) ? 1 : 0);

    public SimilarityScore? Score(SimilarSignals seed, SimilarSignals candidate)
    {
        var w = _options.Weights;
        double earned = 0;
        var available = 0;
        var reasons = new List<string>();
        var halfDouble = false;
        double? bpmDiff = null;

        // ---- genre ----
        if (_genres.Compare(seed.GenreIds, candidate.GenreIds) is { } genre)
        {
            available += w.Genre;
            earned += w.Genre * genre.Value;
            if (genre.Reason is not null) reasons.Add(genre.Reason);
        }

        // ---- tempo ----
        if (seed.Bpm is > 0 && candidate.Bpm is > 0)
        {
            available += w.Bpm;
            var (value, reason, usedHalfDouble, diff) = CompareTempo(seed.Bpm.Value, candidate.Bpm.Value);
            earned += w.Bpm * value;
            bpmDiff = diff;
            halfDouble = usedHalfDouble && value > 0;
            if (reason is not null) reasons.Add(reason);
        }

        // ---- key ----
        if (CamelotKey.TryParse(seed.MusicalKey, out var seedKey) && CamelotKey.TryParse(candidate.MusicalKey, out var candidateKey))
        {
            available += w.Key;
            var key = CamelotKey.Compare(seedKey, candidateKey);
            earned += w.Key * key.Value;
            if (key.Reason is not null) reasons.Add(key.Reason);
        }

        // ---- DJ tags (only informative when the seed has any) ----
        if (seed.TagIds.Count > 0)
        {
            available += w.Tags;
            var shared = seed.TagIds.Intersect(candidate.TagIds).ToList();
            var union = seed.TagIds.Union(candidate.TagIds).Count();
            earned += w.Tags * (union == 0 ? 0 : (double)shared.Count / union);
            if (shared.Count > 0)
                reasons.Add("shared tag: " + string.Join(", ", shared.Select(id => _tagNames.TryGetValue(id, out var n) ? n : "?").Order(StringComparer.OrdinalIgnoreCase)));
        }

        if (available < _options.MinAvailableWeight || available == 0) return null;

        // Observed context, reported but not scored: there is no validated weight for it.
        var sameArtist = seed.Artist is { Length: > 0 } a && string.Equals(a, candidate.Artist, StringComparison.OrdinalIgnoreCase);
        if (sameArtist) reasons.Add("same artist");
        if (seed.Year is > 0 && candidate.Year is > 0 && seed.Year / 10 == candidate.Year / 10) reasons.Add($"same decade ({seed.Year / 10 * 10}s)");

        var score = (int)Math.Round(100 * earned / available, MidpointRounding.AwayFromZero);
        return new SimilarityScore(score, available, reasons, halfDouble, bpmDiff, sameArtist);
    }

    /// <summary>
    /// The tempo match: linear from 1 at the same BPM to 0 at the window's edge. With half/double on, the seed's tempo doubled and halved
    /// are tried too (70 and 140 mix); the direct comparison wins a tie so a half/double label only appears when it is really what matched.
    /// </summary>
    private (double Value, string? Reason, bool HalfDouble, double Diff) CompareTempo(double seed, double candidate)
    {
        var window = Math.Max(_options.BpmWindow, 0.1);
        var direct = candidate - seed;
        var best = (Diff: direct, Factor: 1.0, Label: (string?)null);

        if (_options.AllowHalfDoubleTempo)
        {
            foreach (var (factor, label) in new[] { (2.0, "×2"), (0.5, "½") })
            {
                var diff = candidate - seed * factor;
                if (Math.Abs(diff) < Math.Abs(best.Diff)) best = (diff, factor, label);
            }
        }

        var value = Math.Max(0, 1 - Math.Abs(best.Diff) / window);
        if (value <= 0) return (0, null, false, Math.Abs(best.Diff));

        var amount = best.Diff.ToString("+0.#;-0.#;+0", CultureInfo.InvariantCulture);
        var reason = best.Label is null
            ? (Math.Abs(best.Diff) < 0.05 ? "same BPM" : $"BPM {amount}")
            : $"BPM {best.Label} ({amount})";
        return (value, reason, best.Label is not null, Math.Abs(best.Diff));
    }

    /// <summary>
    /// The order of results: higher score, then more evidence behind it, the same artist, a direct (not half/double) tempo match, the closer tempo, then the title and finally the id,
    /// so equal inputs always give the same list.
    /// </summary>
    public static int Compare((SimilarSignals Track, SimilarityScore Score) x, (SimilarSignals Track, SimilarityScore Score) y)
    {
        var c = y.Score.Score.CompareTo(x.Score.Score);
        if (c != 0) return c;
        c = y.Score.AvailableWeight.CompareTo(x.Score.AvailableWeight);
        if (c != 0) return c;
        c = y.Score.SameArtist.CompareTo(x.Score.SameArtist);
        if (c != 0) return c;
        // A direct tempo match is a stronger result than one that needed half or double time.
        c = x.Score.UsedHalfDoubleTempo.CompareTo(y.Score.UsedHalfDoubleTempo);
        if (c != 0) return c;
        c = (x.Score.BpmDifference ?? double.MaxValue).CompareTo(y.Score.BpmDifference ?? double.MaxValue);
        if (c != 0) return c;
        c = string.Compare(x.Track.Title, y.Track.Title, StringComparison.OrdinalIgnoreCase);
        return c != 0 ? c : x.Track.TrackId.CompareTo(y.Track.TrackId);
    }
}
