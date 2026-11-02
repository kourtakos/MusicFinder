using System.Text.RegularExpressions;
using MusicFinder.Application.Search;
using MusicFinder.Application.Services;

namespace MusicFinder.Application.Duplicates;

/// <summary>
/// What a title and an artist say about the song itself, apart from the version: "Frozen (Radio Edit) feat. X" is the song
/// "frozen", by "madonna", in the version "radio edit". Greek and Greeklish spellings of the same words get the same phonetic key.
/// </summary>
public sealed record ParsedSong(
    string ArtistKey, string TitleKey, IReadOnlyList<string> Versions, IReadOnlyList<string> Featured, string BaseTitle, string MainArtist)
{
    /// <summary>The versions as one comparable text; empty for the plain song.</summary>
    public string VersionText => string.Join("|", Versions);
    public string SongKey => ArtistKey + "|" + TitleKey;
}

public static partial class SongIdentity
{
    [GeneratedRegex(@"\(([^()]*)\)|\[([^\[\]]*)\]")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"\s*(?:\bfeat\.?|\bft\.?|\bfeaturing)\s+(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex FeatureSuffix();

    [GeneratedRegex(@"\s+(?:feat\.?|ft\.?|featuring)\s+|\s*;\s*|\s*,\s*|\s+&\s+|\s+x\s+|\s+vs\.?\s+|\s+and\s+|\s*/\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ArtistSeparators();

    [GeneratedRegex(@"\s+-\s+")]
    private static partial Regex DashSplit();

    public static ParsedSong Parse(string? artist, string? title)
    {
        var featured = new List<string>();
        var versions = new SortedSet<string>(StringComparer.Ordinal);

        var rest = title ?? "";
        // "(...)" and "[...]": featured artists and version words are taken out; any other bracket stays part of the title.
        rest = Brackets().Replace(rest, m =>
        {
            var inner = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim();
            var fm = FeatureSuffixInBracket(inner);
            if (fm is not null) { featured.AddRange(SplitArtists(fm)); return " "; }
            var tokens = VersionTokens(inner);
            if (tokens is null) return m.Value;
            foreach (var t in tokens) versions.Add(t);
            return " ";
        });

        // "Title - Something Mix": the last dash segment, when it is a version.
        var parts = DashSplit().Split(rest);
        if (parts.Length > 1)
        {
            var tokens = VersionTokens(parts[^1]);
            if (tokens is not null)
            {
                foreach (var t in tokens) versions.Add(t);
                rest = string.Join(" - ", parts[..^1]);
            }
        }

        var fs = FeatureSuffix().Match(rest);
        if (fs.Success) { featured.AddRange(SplitArtists(fs.Groups[1].Value)); rest = rest[..fs.Index]; }

        var credited = SplitArtists(FeatureSuffix().Replace(artist ?? "", m => { featured.AddRange(SplitArtists(m.Groups[1].Value)); return ""; }));
        var main = credited.Count > 0 ? credited[0] : "";

        var baseTitle = Regex.Replace(rest, @"\s+", " ").Trim();
        return new ParsedSong(Phonetic(main), Phonetic(baseTitle), versions.ToList(),
            featured.Select(Phonetic).Where(f => f.Length > 0).Distinct().OrderBy(f => f, StringComparer.Ordinal).ToList(), baseTitle, main);
    }

    private static string? FeatureSuffixInBracket(string inner)
    {
        var m = Regex.Match(inner, @"^(?:feat\.?|ft\.?|featuring)\s+(.+)$", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static List<string> SplitArtists(string text) =>
        ArtistSeparators().Split(text).Select(a => a.Trim()).Where(a => a.Length > 0).ToList();

    private static string Phonetic(string text)
    {
        var normalized = TextNormalizer.Normalize(text);
        // keep letters and digits only, so punctuation differences ("Mr. Brightside" / "Mr Brightside") do not matter
        normalized = Regex.Replace(normalized, @"[^\p{L}\p{N}\s]", " ");
        return GreekPhoneticKey.KeyText(Regex.Replace(normalized, @"\s+", " ").Trim());
    }

    /// <summary>
    /// The version words in a piece of text, or null when the text is not about a version at all (so "Part 2" stays in the title).
    /// "Original mix", "album version" and "single version" are the plain song and give an empty list.
    /// </summary>
    internal static IReadOnlyList<string>? VersionTokens(string text)
    {
        var t = Regex.Replace(TextNormalizer.Normalize(text), @"[^\p{L}\p{N}\s]", " ");
        t = Regex.Replace(t, @"\s+", " ").Trim();
        if (t.Length == 0) return null;

        if (t is "original mix" or "original version" or "original" or "album version" or "single version" or "album mix" or "main version")
            return [];

        var found = new List<string>();
        if (Regex.IsMatch(t, @"\bradio (edit|mix|version)\b")) found.Add("radio edit");
        if (Regex.IsMatch(t, @"\bextended\b")) found.Add("extended");
        if (Regex.IsMatch(t, @"\bclub (mix|version|edit)\b")) found.Add("club");
        if (Regex.IsMatch(t, @"\binstrumental\b")) found.Add("instrumental");
        if (Regex.IsMatch(t, @"\bacoustic\b")) found.Add("acoustic");
        if (Regex.IsMatch(t, @"\ba ?cappella\b")) found.Add("acappella");
        if (Regex.IsMatch(t, @"\blive\b")) found.Add("live");
        if (Regex.IsMatch(t, @"\bdemo\b")) found.Add("demo");
        if (Regex.IsMatch(t, @"\bremaster(ed)?\b")) found.Add("remaster");
        if (Regex.IsMatch(t, @"\bdub\b")) found.Add("dub");
        if (Regex.IsMatch(t, @"\b(clean|explicit)\b")) found.Add(t.Contains("clean") ? "clean" : "explicit");

        var remix = Regex.Match(t, @"^(.*?)\s*\b(remix|rework|bootleg|vip)\b");
        if (remix.Success) found.Add("remix:" + remix.Groups[1].Value.Trim());
        else if (found.Count == 0)
        {
            var edit = Regex.Match(t, @"^(.+?)\s+(edit|mix|version)$");
            if (edit.Success) found.Add(edit.Groups[2].Value + ":" + edit.Groups[1].Value);
            else if (t is "edit" or "mix") found.Add(t);
        }
        return found.Count == 0 ? null : found;
    }
}
