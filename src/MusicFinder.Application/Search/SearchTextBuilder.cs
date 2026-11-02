using MusicFinder.Application.Services;
using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Search;

/// <summary>
/// Prepares the text of one track for smart Greek search: normalized words per field, the phonetic key of the same words, and
/// the user's own aliases that apply to the track. Pure; the index job decides when to run it.
/// </summary>
public static class SearchTextBuilder
{
    /// <summary>
    /// Bump this when the normalization or the phonetic key changes: every alias row is then rebuilt on the next start
    /// (the same mechanism as the metadata reader version).
    /// </summary>
    public const int CurrentVersion = 1;

    public static TrackSearchAlias Build(SearchAliasStaleRow track, IReadOnlyList<SearchAliasOverride> overrides)
    {
        var title = TextNormalizer.Normalize(track.Title);
        var artist = TextNormalizer.Normalize(track.Artist + " " + track.AlbumArtist);
        var album = TextNormalizer.Normalize(track.Album);
        var genre = TextNormalizer.Normalize(track.GenreRaw);
        var file = FileText(track.FullPath, track.FileName);

        return new TrackSearchAlias(
            track.Id,
            title, artist, album, genre, file,
            GreekPhoneticKey.KeyText(title), GreekPhoneticKey.KeyText(artist), GreekPhoneticKey.KeyText(album),
            GreekPhoneticKey.KeyText(genre), GreekPhoneticKey.KeyText(file),
            ExtraText(overrides, title, artist, album, genre, file));
    }

    /// <summary>The file name without its extension plus the folder names above it ("Λαϊκά" in a path is a reason to find a track).</summary>
    public static string FileText(string fullPath, string fileName)
    {
        var parts = new List<string> { Path.GetFileNameWithoutExtension(fileName) };
        var segments = fullPath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        // Drop the file itself (last) and a drive letter such as "C:" (first).
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (i == 0 && segments[i].EndsWith(':')) continue;
            parts.Add(segments[i]);
        }
        return TextNormalizer.BuildSearchText(parts.ToArray());
    }

    private static string ExtraText(IReadOnlyList<SearchAliasOverride> overrides, params string[] fields)
    {
        if (overrides.Count == 0) return "";

        // Whole-word containment: pad with spaces so "pink" does not match inside "pinkie".
        var haystack = " " + string.Join(' ', fields.Where(f => f.Length > 0)) + " ";
        var extra = new List<string>();
        foreach (var o in overrides)
        {
            if (o.SourceNormalized.Length == 0 || !haystack.Contains(" " + o.SourceNormalized + " ", StringComparison.Ordinal)) continue;
            extra.Add(o.AliasNormalized);
            extra.Add(GreekPhoneticKey.KeyText(o.AliasNormalized));
        }
        return string.Join(' ', extra.Distinct());
    }
}
