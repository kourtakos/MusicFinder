using System.Text;
using System.Text.RegularExpressions;
using MusicFinder.Domain.Entities;

namespace MusicFinder.Application.Identification;

/// <summary>What a tag search looks for, and where each part came from (shown to the user as evidence).</summary>
public sealed record TagSearchTerms(string Title, string? Artist, string? Album, bool FromFileName);

/// <summary>
/// Builds the MusicBrainz (Lucene) search text from a track's tags. Values go inside quoted phrases, where only the
/// backslash and the double quote have a meaning, so a title such as <c>Don't Stop (Remix) [2011]: AC/DC*</c> can never change the query's structure.
/// </summary>
public static partial class MusicBrainzQueryBuilder
{
    /// <summary>Null when there is not even a title to search for.</summary>
    public static TagSearchTerms? TermsFor(Track track)
    {
        var title = Clean(track.Title);
        if (title is not null)
            return new TagSearchTerms(title, Clean(track.Artist) ?? Clean(track.AlbumArtist), Clean(track.Album), FromFileName: false);

        // No title tag: try "Artist - Title.ext", the most common naming of untagged files.
        var name = Path.GetFileNameWithoutExtension(track.FileName ?? "");
        name = LeadingNumber().Replace(name, "").Replace('_', ' ').Trim();
        if (name.Length == 0) return null;
        var split = name.IndexOf(" - ", StringComparison.Ordinal);
        if (split > 0 && split + 3 < name.Length)
            return new TagSearchTerms(name[(split + 3)..].Trim(), Clean(track.Artist) ?? name[..split].Trim(), Clean(track.Album), FromFileName: true);
        return new TagSearchTerms(name, Clean(track.Artist), Clean(track.Album), FromFileName: true);
    }

    public static string Build(TagSearchTerms terms)
    {
        var q = new StringBuilder("recording:").Append(Phrase(terms.Title));
        if (terms.Artist is not null) q.Append(" AND artist:").Append(Phrase(terms.Artist));
        if (terms.Album is not null) q.Append(" AND release:").Append(Phrase(terms.Album));
        return q.ToString();
    }

    /// <summary>A quoted phrase with the two characters that matter inside quotes escaped.</summary>
    public static string Phrase(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    [GeneratedRegex(@"^\s*\d{1,3}\s*[-._)]*\s+")]
    private static partial Regex LeadingNumber();
}
