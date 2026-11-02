using System.Globalization;
using MusicFinder.Application.Tags;
using MusicFinder.Domain.Entities;
using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Identification;

/// <summary>
/// Turns a chosen recording (and, optionally, a chosen release) into per-field proposals next to the track's current tags.
/// Pure. It never invents values: BPM and key are not offered at all, genres are only a suggestion that starts unticked,
/// and a field that already holds a different value starts unticked too, so nothing is replaced unless the user ticks it.
/// </summary>
public static class IdentificationProposalBuilder
{
    public const string FromRecording = "MusicBrainz recording";
    public const string FromRelease = "MusicBrainz release";
    public const string FromCommunityTags = "MusicBrainz community genres";
    public const string FromArtistTags = "MusicBrainz genres of the artist";
    public const int MaxGenres = 3;

    public static IReadOnlyList<FieldProposal> Build(
        Track track, IdentificationCandidate recording, ReleaseDetails? release, bool suggestGenres = true)
    {
        var list = new List<FieldProposal>();

        Add(list, TagField.Title, track.Title, recording.Title, FromRecording);
        Add(list, TagField.Artist, track.Artist, recording.ArtistCredit, FromRecording);

        if (release is not null)
        {
            Add(list, TagField.Album, track.Album, release.Title, FromRelease);
            Add(list, TagField.AlbumArtist, track.AlbumArtist, release.AlbumArtistCredit, FromRelease);
            Add(list, TagField.Year, track.Year?.ToString(CultureInfo.InvariantCulture), YearOf(release.Date), FromRelease);
            var position = release.Tracks.FirstOrDefault(t => string.Equals(t.RecordingId, recording.RecordingId, StringComparison.OrdinalIgnoreCase));
            if (position is not null)
                Add(list, TagField.TrackNumber, track.TrackNumber?.ToString(CultureInfo.InvariantCulture),
                    position.Position.ToString(CultureInfo.InvariantCulture), FromRelease);
        }

        if (suggestGenres && recording.GenreSuggestions.Count > 0)
        {
            var proposed = string.Join("; ", recording.GenreSuggestions.Take(MaxGenres).Select(TitleCase));
            // Community genres are rough: they fill a file that has no genre, but never replace one the user chose.
            if (!Same(track.GenreRaw, proposed))
                list.Add(new FieldProposal(TagField.Genre, track.GenreRaw, proposed, Selected: string.IsNullOrWhiteSpace(track.GenreRaw),
                    recording.GenreLevel == "artist" ? FromArtistTags : FromCommunityTags));
        }

        // Linking the file to its recording is useful and harmless, so it starts ticked unless it is already there.
        if (!Same(track.MusicBrainzRecordingId, recording.RecordingId))
            list.Add(new FieldProposal(TagField.MusicBrainzRecordingId, track.MusicBrainzRecordingId, recording.RecordingId, Selected: true, FromRecording));

        return list;
    }

    /// <summary>"classic rock" -> "Classic Rock": MusicBrainz writes genres in lower case.</summary>
    private static string TitleCase(string genre) =>
        System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(genre.Trim().ToLowerInvariant());

    public static TagChangeSet ToChangeSet(IEnumerable<FieldProposal> proposals) =>
        new(proposals.Where(p => p.Selected).ToDictionary(p => p.Field, p => p.Proposed));

    /// <summary>The year of a MusicBrainz date ("1985", "1985-03", "1985-03-02"), only if it is a plausible one.</summary>
    public static string? YearOf(string? date)
    {
        if (date is null || date.Length < 4) return null;
        return int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y)
               && y is >= TagChangeValidator.MinYear and <= TagChangeValidator.MaxYear
            ? y.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    private static void Add(List<FieldProposal> list, TagField field, string? current, string? proposed, string source)
    {
        if (string.IsNullOrWhiteSpace(proposed) || Same(current, proposed)) return;
        var empty = string.IsNullOrWhiteSpace(current);
        list.Add(new FieldProposal(field, current, proposed.Trim(), Selected: empty, source));
    }

    private static bool Same(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}
