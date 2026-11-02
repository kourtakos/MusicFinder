using MusicFinder.Application.Services;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Entities;

namespace MusicFinder.Application.Identification;

/// <summary>
/// Facts about how a candidate compares with the file in hand, worked out locally and shown next to the service's own score.
/// They are observations ("duration matches"), never a replacement for the score and never a combined confidence figure.
/// </summary>
public static class CandidateEvidence
{
    public static readonly TimeSpan DurationMatches = TimeSpan.FromSeconds(3);

    /// <param name="fileNameTerms">When the track has no title tag and the search used its file name, those terms are what the candidate is compared with.</param>
    public static IReadOnlyList<string> Compute(Track track, MbRecording recording, TagSearchTerms? fileNameTerms = null)
    {
        var list = new List<string>();

        if (string.Equals(track.MusicBrainzRecordingId, recording.Id, StringComparison.OrdinalIgnoreCase))
            list.Add("already tagged with this id");

        if (track.DurationMs is { } mine && recording.DurationMs is { } theirs)
        {
            var delta = TimeSpan.FromMilliseconds(Math.Abs(mine - theirs));
            list.Add(delta <= DurationMatches
                ? $"duration matches ({Clock(mine)})"
                : $"duration differs by {Seconds(delta)} s (file {Clock(mine)}, recording {Clock(theirs)})");
        }
        else if (recording.DurationMs is null)
        {
            list.Add("MusicBrainz has no duration for this recording");
        }

        var fromName = fileNameTerms is { FromFileName: true };
        var source = fromName ? "your file name" : "your tag";
        Compare(list, "title", fromName ? fileNameTerms!.Title : track.Title, recording.Title, source);
        Compare(list, "artist", fromName ? fileNameTerms!.Artist ?? track.Artist : track.Artist ?? track.AlbumArtist, recording.ArtistCredit, source);
        return list;
    }

    private static void Compare(List<string> list, string what, string? mine, string theirs, string source)
    {
        if (string.IsNullOrWhiteSpace(mine)) return;
        list.Add(TextNormalizer.Normalize(mine) == TextNormalizer.Normalize(theirs)
            ? $"{what} matches {source}"
            : $"{what} differs from {source}");
    }

    private static string Clock(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    private static string Seconds(TimeSpan span) => Math.Round(span.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
