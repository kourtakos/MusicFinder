using MusicFinder.Application.Search;

namespace MusicFinder.Infrastructure.Search;

/// <summary>
/// The MATCH expressions of one search, from strictest to loosest. A row belongs to the first tier whose expression matches it, which is
/// what the relevance sort ranks by. Null/empty parts are not applicable (for example there is no keyboard tier when nothing needs one).
/// </summary>
/// <param name="Exact">On TrackSearch: the words as typed, in the original columns (case-insensitive, Latin accents folded by the tokenizer).</param>
/// <param name="ExactPrimary">The same, but only in title / artist / album columns.</param>
/// <param name="Legacy">On TrackSearch: the pre-M7 expression (raw or normalized). Keeps tracks searchable while their alias row is still being built.</param>
/// <param name="Tier1">On TrackAliasSearch: normalized words (accents, case, final sigma ignored).</param>
/// <param name="Tier2">Tier 1 plus the other-keyboard-layout reading.</param>
/// <param name="Tier3">Tier 2 plus phonetic keys (Greek and Greeklish meet).</param>
/// <param name="All">Tier 3 plus typo candidates. The union of everything that may match.</param>
/// <param name="Primary">On TrackAliasSearch: all forms, restricted to title / artist / album (and the user's own aliases).</param>
public sealed record FtsMatchSet(
    string? Exact, string? ExactPrimary, string? Legacy, string? Tier1, string? Tier2, string? Tier3, string? All, string? Primary)
{
    public bool IsEmpty => All is null && Legacy is null;
}

/// <summary>
/// Builds the safe FTS5 MATCH strings for an <see cref="ExpandedQuery"/>. Every user-derived value is emitted as a double-quoted string
/// (embedded quotes doubled), column names come from a fixed table, and the result is passed as a SQL parameter, never concatenated into SQL.
/// </summary>
public static class FtsMatchBuilder
{
    // Columns of the original index (TrackSearch).
    private static readonly Dictionary<string, string> RawColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["artist"] = "Artist", ["title"] = "Title", ["album"] = "Album", ["genre"] = "GenreRaw"
    };

    // Columns of the alias index (TrackAliasSearch): normalized (N) and phonetic key (K). Extra holds the user's own aliases in both forms.
    private static readonly Dictionary<string, (string N, string K)> AliasColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["artist"] = ("{ArtistN Extra}", "{ArtistK Extra}"),
        ["title"] = ("{TitleN Extra}", "{TitleK Extra}"),
        ["album"] = ("{AlbumN Extra}", "{AlbumK Extra}"),
        ["genre"] = ("{GenreN}", "{GenreK}")
    };

    private const string AllN = "{TitleN ArtistN AlbumN GenreN FileN Extra}";
    private const string AllK = "{TitleK ArtistK AlbumK GenreK FileK Extra}";
    private const string PrimaryN = "{TitleN ArtistN AlbumN Extra}";
    private const string PrimaryK = "{TitleK ArtistK AlbumK Extra}";
    private const string PrimaryRaw = "{Title Artist AlbumArtist Album}";

    public static FtsMatchSet Build(ExpandedQuery query)
    {
        if (query.IsEmpty) return new FtsMatchSet(null, null, null, null, null, null, null, null);

        var exact = new List<string>();
        var exactPrimary = new List<string>();
        var legacy = new List<string>();
        var t1 = new List<string>();
        var t2 = new List<string>();
        var t3 = new List<string>();
        var all = new List<string>();
        var primary = new List<string>();
        var primaryApplicable = true;

        foreach (var t in query.Terms)
        {
            var star = t.IsPhrase ? "" : "*";
            var scoped = t.Field is not null && RawColumns.ContainsKey(t.Field);
            var rawCol = scoped ? RawColumns[t.Field!] : null;
            var (nCols, kCols) = scoped ? AliasColumns[t.Field!] : (AllN, AllK);

            // --- original index ---
            var raw = $"\"{Escape(t.Raw)}\"{star}";
            var norm = $"\"{Escape(t.N)}\"{star}";
            exact.Add(rawCol is null ? raw : $"{rawCol}:{raw}");
            exactPrimary.Add(scoped
                ? (t.Field!.Equals("genre", StringComparison.OrdinalIgnoreCase) ? "" : $"{rawCol}:{raw}")
                : $"{PrimaryRaw}:{raw}");
            legacy.Add(rawCol is null
                ? $"({raw} OR NormalizedText:{norm})"
                : raw == norm ? $"{rawCol}:{raw}" : $"({rawCol}:{raw} OR {rawCol}:{norm})");

            // --- alias index, tier by tier ---
            var n1 = Alias(nCols, t.N, star);
            t1.Add(n1);

            var tier2 = new List<string> { n1 };
            if (t.KB is not null) tier2.Add(Alias(nCols, t.KB, star));
            t2.Add(Group(tier2));

            var tier3 = new List<string>(tier2);
            if (t.K is not null) tier3.Add(Alias(kCols, t.K, KeyStar(t)));
            if (t.KBK is not null) tier3.Add(Alias(kCols, t.KBK, KeyStar(t)));
            t3.Add(Group(tier3));

            var tier4 = new List<string>(tier3);
            tier4.AddRange(t.Fuzzy.Select(f => Alias(nCols, f, "*")));
            all.Add(Group(tier4));

            // --- "primary" = the match is in title / artist / album (or the user's alias), not in the path or genre ---
            if (scoped && t.Field!.Equals("genre", StringComparison.OrdinalIgnoreCase)) primaryApplicable = false;
            else
            {
                var pn = scoped ? nCols : PrimaryN;
                var pk = scoped ? kCols : PrimaryK;
                var forms = new List<string> { Alias(pn, t.N, star) };
                if (t.KB is not null) forms.Add(Alias(pn, t.KB, star));
                if (t.K is not null) forms.Add(Alias(pk, t.K, KeyStar(t)));
                if (t.KBK is not null) forms.Add(Alias(pk, t.KBK, KeyStar(t)));
                forms.AddRange(t.Fuzzy.Select(f => Alias(pn, f, "*")));
                primary.Add(Group(forms));
            }
        }

        string? Join(List<string> parts) => parts.Count == 0 || parts.Any(p => p.Length == 0) ? null : string.Join(" AND ", parts);

        var tier1 = Join(t1);
        var tier2Str = Join(t2);
        var tier3Str = Join(t3);
        var allStr = Join(all);
        return new FtsMatchSet(
            Join(exact), Join(exactPrimary), Join(legacy),
            tier1,
            tier2Str == tier1 ? null : tier2Str,           // identical tiers are not searched twice
            tier3Str == tier2Str ? null : tier3Str,
            allStr,
            primaryApplicable ? Join(primary) : null);
    }

    /// <summary>A phonetic key is matched as a prefix only when it is long enough to be specific; "th" must not pull in every θε… track.</summary>
    private static string KeyStar(ExpandedTerm t) => !t.IsPhrase && (t.K ?? t.KBK ?? "").Length >= 3 ? "*" : "";

    private static string Alias(string cols, string value, string star) => $"{cols}:\"{Escape(value)}\"{star}";

    private static string Group(List<string> forms) => forms.Count == 1 ? forms[0] : "(" + string.Join(" OR ", forms) + ")";

    private static string Escape(string value) => value.Replace("\"", "\"\"");
}
