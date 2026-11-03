using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Dj;

public static class HistoryMatchMethod
{
    public const string Path = "Path";
    public const string NameAndSize = "NameAndSize";
    public const string NameAndDuration = "NameAndDuration";
    public const string Manual = "Manual";
}

public static class HistoryUnmatchedReason
{
    public const string NotInLibrary = "NotInLibrary";
    public const string Ambiguous = "Ambiguous";
}

/// <summary>The outcome of matching: a track, or why there is none. <see cref="Candidates"/> lists the tracks of an ambiguous match.</summary>
public sealed record HistoryMatch(long? TrackId, string? Method, double Confidence, string? Reason, IReadOnlyList<long> Candidates)
{
    public bool IsMatch => TrackId is not null;
}

/// <summary>
/// Ties a play reported by a DJ program to a library track. The path comes first. When the file sits elsewhere in the library (another drive
/// letter, a moved folder) the file name with its size, then with its duration, still point to it, but only when exactly one track fits.
/// Artist and title alone are never enough: that guess would need the user's review.
/// </summary>
public sealed class HistoryTrackMatcher
{
    public const double DurationToleranceSeconds = 1.5;

    private readonly IHistoryTrackLookup _lookup;

    public HistoryTrackMatcher(IHistoryTrackLookup lookup) => _lookup = lookup;

    public async Task<HistoryMatch> MatchAsync(ImportedPlayEvent play, CancellationToken ct)
    {
        var path = await _lookup.FindByPathAsync(play.FilePath, ct);
        if (path.Count > 0) return Decide(path, HistoryMatchMethod.Path, 1.0);

        var name = FileNameOf(play.FilePath);
        if (name.Length == 0) return NotFound();

        if (play.FileSize is { } size)
        {
            var bySize = await _lookup.FindByNameAndSizeAsync(name, size, ct);
            if (bySize.Count > 0) return Decide(bySize, HistoryMatchMethod.NameAndSize, 0.9);
        }

        if (play.DurationSeconds is { } seconds && seconds > 0)
        {
            var byDuration = await _lookup.FindByNameAndDurationAsync(name, seconds, DurationToleranceSeconds, ct);
            if (byDuration.Count > 0) return Decide(byDuration, HistoryMatchMethod.NameAndDuration, 0.8);
        }
        return NotFound();
    }

    private static HistoryMatch Decide(IReadOnlyList<long> ids, string method, double confidence) =>
        ids.Count == 1
            ? new HistoryMatch(ids[0], method, confidence, null, ids)
            : new HistoryMatch(null, null, 0, HistoryUnmatchedReason.Ambiguous, ids);

    private static HistoryMatch NotFound() => new(null, null, 0, HistoryUnmatchedReason.NotInLibrary, []);

    /// <summary>The last part of a Windows or Unix path.</summary>
    public static string FileNameOf(string path)
    {
        var cut = path.LastIndexOfAny(['\\', '/']);
        return (cut >= 0 ? path[(cut + 1)..] : path).Trim();
    }
}
