using System.Globalization;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Duplicates;

/// <summary>
/// Finds tracks that are probably the same recording (same artist, title and version, lengths within a few seconds) and, apart from
/// those, songs that exist in several versions. These are suspicions for a person to review, never conclusions: the groups
/// are only listed and nothing is merged, moved or deleted.
/// </summary>
public sealed class ProbableDuplicateService : IProbableDuplicateService
{
    /// <summary>Two lengths this close count as the same recording.</summary>
    public const int DurationToleranceMs = 3000;

    private static readonly HashSet<string> Lossless = new(StringComparer.OrdinalIgnoreCase) { ".flac", ".wav", ".aiff", ".aif", ".alac" };

    private readonly ISongCandidateStore _store;

    public ProbableDuplicateService(ISongCandidateStore store) => _store = store;

    public Task DismissAsync(string key, CancellationToken ct) => _store.DismissAsync(key, ct);

    public async Task<ProbableDuplicateResult> FindAsync(CancellationToken ct)
    {
        var candidates = await _store.GetCandidatesAsync(ct);
        var dismissed = await _store.GetDismissedKeysAsync(ct);
        return Group(candidates, dismissed);
    }

    /// <summary>The pure grouping; public so it can be tested without a database.</summary>
    public static ProbableDuplicateResult Group(IEnumerable<SongCandidate> candidates, IReadOnlySet<string> dismissed)
    {
        var parsed = candidates
            .Where(c => !string.IsNullOrWhiteSpace(c.Artist) && !string.IsNullOrWhiteSpace(c.Title))
            .Select(c => (Track: c, Song: SongIdentity.Parse(c.Artist, c.Title)))
            .Where(x => x.Song.ArtistKey.Length > 0 && x.Song.TitleKey.Length > 0)
            .ToList();

        var same = new List<SongGroup>();
        var other = new List<SongGroup>();

        foreach (var song in parsed.GroupBy(x => x.Song.SongKey, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            var variants = 0;
            foreach (var version in song.GroupBy(x => x.Song.VersionText, StringComparer.Ordinal))
            {
                var key = "same|" + song.Key + "|" + version.Key;
                var known = version.Where(x => x.Track.DurationMs is > 0).OrderBy(x => x.Track.DurationMs).ThenBy(x => x.Track.TrackId).ToList();
                var clusters = new List<List<(SongCandidate Track, ParsedSong Song)>>();
                foreach (var x in known)
                {
                    if (clusters.Count > 0 && x.Track.DurationMs!.Value - clusters[^1][0].Track.DurationMs!.Value <= DurationToleranceMs)
                        clusters[^1].Add(x);
                    else clusters.Add([x]);
                }
                variants += clusters.Count + (version.Any(x => x.Track.DurationMs is not > 0) ? 1 : 0);

                if (dismissed.Contains(key)) continue;
                foreach (var cluster in clusters.Where(c => c.Count > 1))
                    same.Add(Build(key, SongGroupKind.SameRecording, cluster));
            }

            var otherKey = "versions|" + song.Key;
            if (variants > 1 && !dismissed.Contains(otherKey))
                other.Add(Build(otherKey, SongGroupKind.OtherVersions, song.ToList()));
        }

        return new ProbableDuplicateResult(
            same.OrderBy(g => g.Heading, StringComparer.CurrentCultureIgnoreCase).ThenBy(g => g.Members[0].TrackId).ToList(),
            other.OrderBy(g => g.Heading, StringComparer.CurrentCultureIgnoreCase).ToList());
    }

    private static SongGroup Build(string key, SongGroupKind kind, List<(SongCandidate Track, ParsedSong Song)> members)
    {
        var first = members.OrderBy(m => m.Track.TrackId).First();
        var heading = first.Song.MainArtist + " — " + first.Song.BaseTitle;
        var (suggested, reason) = BestQuality(members.Select(m => m.Track).ToList());
        var query = $"artist:\"{Quote(first.Song.MainArtist)}\" title:\"{Quote(first.Song.BaseTitle)}\"";
        var list = members.OrderBy(m => m.Song.VersionText, StringComparer.Ordinal).ThenBy(m => m.Track.DurationMs ?? 0).ThenBy(m => m.Track.TrackId)
            .Select(m => new SongMember(m.Track.TrackId, m.Track.FullPath, m.Track.Extension, m.Track.Artist, m.Track.Title, m.Track.DurationMs,
                m.Track.BitrateKbps, m.Track.Codec, m.Track.PlayCount, VersionLabel(m.Song, m.Track.DurationMs)))
            .ToList();
        return new SongGroup(key, kind, heading, list, suggested, reason, query);
    }

    private static string Quote(string s) => s.Replace("\"", " ");

    private static string VersionLabel(ParsedSong song, int? durationMs)
    {
        var version = song.Versions.Count == 0 ? "Plain version" : string.Join(", ", song.Versions.Select(v => v.Replace(":", " ")));
        return durationMs is > 0 ? $"{version} · {Duration(durationMs.Value)}" : version;
    }

    internal static string Duration(int ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return ((int)t.TotalMinutes).ToString(CultureInfo.InvariantCulture) + ":" + t.Seconds.ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>A suggestion for the file to keep when reviewing: lossless over lossy, then the higher bitrate, then more plays. Not a verdict on audio quality.</summary>
    public static (long TrackId, string Reason) BestQuality(IReadOnlyList<SongCandidate> tracks)
    {
        var best = tracks
            .OrderByDescending(t => Lossless.Contains(t.Extension) ? 1 : 0)
            .ThenByDescending(t => t.BitrateKbps ?? 0)
            .ThenByDescending(t => t.PlayCount)
            .ThenBy(t => t.TrackId)
            .First();
        var reason = Lossless.Contains(best.Extension) && tracks.Any(t => !Lossless.Contains(t.Extension))
            ? "lossless format"
            : tracks.Select(t => t.BitrateKbps ?? 0).Distinct().Count() > 1 && best.BitrateKbps is > 0
                ? $"highest listed bitrate ({best.BitrateKbps} kbps)"
                : best.PlayCount > 0 && tracks.Any(t => t.PlayCount < best.PlayCount) ? $"most plays ({best.PlayCount})" : "nothing separates them; the first in the library";
        return (best.TrackId, reason);
    }
}
