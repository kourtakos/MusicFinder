using MusicFinder.Application.Services;
using MusicFinder.Domain.Entities;
using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Identification;

/// <summary>What the automatic choice for one track says, and why. Only a confident choice is ticked for the user.</summary>
public sealed record AutoDecision(IdentificationCandidate? Candidate, bool Confident, string Reason);

/// <summary>
/// The choices made for the user when many tracks are identified at once: which candidate, which release, and whether the
/// match is clear enough to tick the track without asking. Pure and conservative: when in doubt the track is left for the
/// user to decide, and nothing is ever written without the preview and confirmation that follow.
/// The services' scores are never mixed: each is judged on its own scale, by its own threshold.
/// </summary>
public static class AutoChoice
{
    /// <summary>AcoustID: the best match must reach this score ...</summary>
    public const double MinAcoustIdScore = 0.90;
    /// <summary>... and beat the second-best match by at least this much (otherwise two recordings are too close to call).</summary>
    public const double MinAcoustIdGap = 0.10;
    /// <summary>MusicBrainz tag search: the best hit must reach this score, and also agree with the file on title, artist and length.</summary>
    public const int MinSearchScore = 90;

    public static AutoDecision Decide(Track track, IReadOnlyList<IdentificationCandidate> candidates)
    {
        if (candidates.Count == 0) return new AutoDecision(null, false, "no candidate");
        var best = candidates[0];

        switch (best.Method)
        {
            case IdentificationMethod.ExistingRecordingId:
                return new AutoDecision(best, true, "the file's own MusicBrainz id");

            case IdentificationMethod.AcoustIdFingerprint:
            {
                var score = best.AcoustIdScore ?? 0;
                if (score < MinAcoustIdScore)
                    return new AutoDecision(best, false, $"the audio match is only {score.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}");
                if (candidates.Count > 1 && candidates[1].AcoustIdScore is { } second && score - second < MinAcoustIdGap)
                    return new AutoDecision(best, false, "two recordings match almost equally well");
                return new AutoDecision(best, true, "a clear audio match");
            }

            default:
            {
                var score = best.MusicBrainzSearchScore ?? 0;
                if (score < MinSearchScore)
                    return new AutoDecision(best, false, $"the search score is only {score} of 100");
                if (string.IsNullOrWhiteSpace(track.Title))
                    return new AutoDecision(best, false, "the file has no title tag, so the match rests on its file name");
                if (!SameText(track.Title, best.Title))
                    return new AutoDecision(best, false, "the title differs from your tag");
                var artist = track.Artist ?? track.AlbumArtist;
                if (!string.IsNullOrWhiteSpace(artist) && !SameText(artist, best.ArtistCredit))
                    return new AutoDecision(best, false, "the artist differs from your tag");
                if (track.DurationMs is { } mine)
                {
                    if (best.DurationMs is not { } theirs)
                        return new AutoDecision(best, false, "MusicBrainz has no length to compare");
                    if (Math.Abs(mine - theirs) > CandidateEvidence.DurationMatches.TotalMilliseconds)
                        return new AutoDecision(best, false, "the length differs");
                }
                return new AutoDecision(best, true, "title, artist and length agree");
            }
        }
    }

    /// <summary>
    /// Releases in the order a DJ library usually wants: official before bootleg, plain albums before compilations and live
    /// releases, the earliest first (the original issue rather than a reissue).
    /// </summary>
    public static IReadOnlyList<ReleaseSummary> OrderReleases(IEnumerable<ReleaseSummary> releases) =>
        releases.OrderBy(r => r.Status == "Official" ? 0 : 1)
                .ThenBy(r => r.PrimaryType == "Album" ? 0 : 1)
                .ThenBy(r => r.SecondaryTypes.Count)
                .ThenBy(r => r.Date ?? "9999", StringComparer.Ordinal)
                .ToList();

    /// <summary>The release whose title is the album already tagged in the file, if any; otherwise the best ordered one.</summary>
    public static ReleaseSummary? PickRelease(Track track, IdentificationCandidate candidate)
    {
        var ordered = OrderReleases(candidate.Releases);
        if (ordered.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(track.Album))
        {
            var same = ordered.FirstOrDefault(r => SameText(r.Title, track.Album));
            if (same is not null) return same;
        }
        return ordered[0];
    }

    private static bool SameText(string? a, string? b) => TextNormalizer.Normalize(a) == TextNormalizer.Normalize(b);
}
