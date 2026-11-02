using System.Globalization;
using System.Text;
using Dapper;
using MusicFinder.Application.Search;
using MusicFinder.Domain.Enums;
using MusicFinder.Domain.Models;

namespace MusicFinder.Infrastructure.Search;

/// <summary>Builds parameterized SQL for paged (keyset) and count queries. No user text is concatenated.</summary>
public static class SearchSqlBuilder
{
    private const string SelectColumns =
        "t.Id, t.FolderId, t.FullPath, t.FileName, t.Extension, t.FileSize, t.Title, t.Artist, t.Album, t.GenreRaw, " +
        "t.Year, t.DurationMs, t.BitrateKbps, t.Bpm, t.MusicalKey, t.Missing, t.ArtworkCacheKey, " +
        "t.Codec, t.SampleRateHz, t.Channels, t.BitDepth, t.IssueFlags, t.ReadError, " +
        // Names are collected per page row; the (TrackId, ...) primary keys make these lookups index-only.
        "(SELECT group_concat(Name, char(31)) FROM (SELECT g.Name FROM TrackGenres tg JOIN Genres g ON g.Id = tg.GenreId " +
        "WHERE tg.TrackId = t.Id ORDER BY g.SortOrder, g.Name)) AS GenreNames, " +
        "(SELECT group_concat(Name, char(31)) FROM (SELECT c.Name FROM TrackCustomTags tc JOIN CustomTags c ON c.Id = tc.TagId " +
        "WHERE tc.TrackId = t.Id ORDER BY c.SortOrder, c.Name)) AS TagNames, " +
        "ps.LastPlayedUtc, ps.LastConfirmedUtc, ps.LastPossibleUtc, " +
        "COALESCE(ps.ConfirmedCount, 0) AS ConfirmedCount, COALESCE(ps.PossibleCount, 0) AS PossibleCount";

    private const string StatsJoin = " LEFT JOIN TrackPlayStats ps ON ps.TrackId = t.Id";

    /// <summary>0 None, 1 Exported, 2 Possible, 3 Confirmed: this track's strongest evidence in the live session.</summary>
    private static string SessionStatusColumn(long? sessionId) => sessionId is null
        ? "0 AS SessionStatus"
        : "CASE WHEN EXISTS (SELECT 1 FROM PlayHistory ph WHERE ph.TrackId = t.Id AND ph.SessionId = @sid AND ph.Status = 'Confirmed' AND ph.SupersededByPlayId IS NULL) THEN 3 " +
          "WHEN EXISTS (SELECT 1 FROM PlayHistory ph WHERE ph.TrackId = t.Id AND ph.SessionId = @sid AND ph.Status = 'Possible' AND ph.SupersededByPlayId IS NULL) THEN 2 " +
          "WHEN EXISTS (SELECT 1 FROM TrackEvents te WHERE te.TrackId = t.Id AND te.SessionId = @sid AND te.EventType = 'Exported') THEN 1 ELSE 0 END AS SessionStatus";

    public static (string Sql, DynamicParameters Parameters) BuildPage(SearchQuery q, ParsedQuery parsed, FtsMatchSet? match = null)
    {
        var (cte, where, p) = BuildWhere(q, parsed, match, forPage: true);
        // "Best match first" has no reverse: a descending relevance sort would just show the worst matches first.
        var direction = q.Sort == SortField.Relevance ? SortDirection.Asc : q.Direction;
        var (expr, numeric) = SortExpression(q.Sort, direction, match);
        var desc = direction == SortDirection.Desc;

        if (q.After is { } cursor)
        {
            var op = desc ? "<" : ">";
            if (cursor.SortValue is null)
            {
                where.Add("t.Id > @lastId");
            }
            else
            {
                if (numeric)
                    p.Add("sortValue", double.Parse(cursor.SortValue, CultureInfo.InvariantCulture));
                else
                    p.Add("sortValue", cursor.SortValue);
                // Ascending: one row-value comparison, so SQLite can seek straight into an index on (sort key, id). Descending mixes directions (key DESC, id ASC), which a row value cannot express.
                where.Add(desc
                    ? $"({expr} {op} @sortValue OR ({expr} = @sortValue AND t.Id > @lastId))"
                    : $"({expr}, t.Id) > (@sortValue, @lastId)");
            }
            p.Add("lastId", cursor.LastId);
        }

        p.Add("limit", Math.Clamp(q.PageSize, 1, 1000) + 1); // one extra row tells us whether more pages exist
        var sql = new StringBuilder()
            .Append(cte)
            // Only the id and the sort key travel through the sorter. The page's full rows (genres, tags, covers...) are read afterwards for
            // the ~200 tracks that survive the LIMIT, not for every match (which can be tens of thousands).
            .Append($"SELECT t.Id, {expr} AS SortKey FROM Tracks t{StatsJoin} WHERE ")
            .Append(string.Join(" AND ", where))
            .Append($" ORDER BY SortKey {(desc ? "DESC" : "ASC")}, t.Id ASC LIMIT @limit")   // the alias: the expression is evaluated once per row
            .ToString();
        return (sql, p);
    }

    public static string BuildByIds(long? sessionId) =>
        $"SELECT {SelectColumns}, {SessionStatusColumn(sessionId)}, '' AS SortKey FROM Tracks t{StatsJoin} WHERE t.Id IN @ids";

    public static (string Sql, DynamicParameters Parameters) BuildCount(SearchQuery q, ParsedQuery parsed, FtsMatchSet? match = null)
    {
        var (cte, where, p) = BuildWhere(q, parsed, match);
        var join = q.Filters.Played is not null || parsed.Played is not null ? StatsJoin : "";
        return ($"{cte}SELECT COUNT(*) FROM Tracks t{join} WHERE {string.Join(" AND ", where)}", p);
    }

    private static (string Cte, List<string> Where, DynamicParameters Parameters) BuildWhere(SearchQuery q, ParsedQuery parsed, FtsMatchSet? match, bool forPage = false)
    {
        var where = new List<string>();
        var p = new DynamicParameters();
        var ctes = new List<string>(); // recursive CTE bodies; folder scope and genre hierarchy can both be present

        // The unary plus keeps the planner off IX_Tracks_Missing: nearly every track has Missing = 0, so that index selects almost the whole table
        // and would force a full sort, instead of walking the title index in order and stopping at the page limit.
        if (!q.Filters.IncludeMissing) where.Add(forPage ? "+t.Missing = 0" : "t.Missing = 0"); // counting can use the index; paging must not

        if (q.FolderId is { } folderId && !q.GlobalScope)
        {
            p.Add("folderId", folderId);
            if (q.IncludeSubfolders)
            {
                ctes.Add("Scope(Id) AS (SELECT @folderId UNION ALL " +
                         "SELECT f.Id FROM Folders f JOIN Scope s ON f.ParentId = s.Id)");
                where.Add("t.FolderId IN (SELECT Id FROM Scope)");
            }
            else
            {
                where.Add("t.FolderId = @folderId");
            }
        }

        if (match is not null && !match.IsEmpty)
        {
            // A track matches if its alias row does (smart search) or, while that row is still being built, if the original index does.
            // One IN over a UNION, not (IN a OR IN b): with an OR the planner cannot start from the matches and scans every track.
            var sources = new List<string>();
            if (match.All is not null)
            {
                p.Add("mAll", match.All);
                sources.Add("SELECT rowid FROM TrackAliasSearch WHERE TrackAliasSearch MATCH @mAll");
            }
            if (match.Legacy is not null)
            {
                p.Add("mLegacy", match.Legacy);
                sources.Add("SELECT rowid FROM TrackSearch WHERE TrackSearch MATCH @mLegacy");
            }
            where.Add($"t.Id IN ({string.Join(" UNION ", sources)})");
            AddRankParameters(p, match);
        }

        // Facets: OR within a facet, AND across facets (and with the parsed query's own filters).
        var f = q.Filters;

        // Genre facet: hierarchy-aware taxonomy ids plus the two virtual buckets, all OR-ed together.
        var genreOr = new List<string>();
        if (f.GenreIds.Count > 0)
        {
            var names = new List<string>();
            for (var i = 0; i < f.GenreIds.Count; i++)
            {
                names.Add($"@gid{i}");
                p.Add($"gid{i}", f.GenreIds[i]);
            }
            ctes.Add($"GenreScope(Id) AS (SELECT Id FROM Genres WHERE Id IN ({string.Join(", ", names)}) UNION ALL " +
                     "SELECT g.Id FROM Genres g JOIN GenreScope s ON g.ParentId = s.Id)");
            genreOr.Add("t.Id IN (SELECT TrackId FROM TrackGenres WHERE GenreId IN (SELECT Id FROM GenreScope))");
        }
        if (f.UnknownGenre) genreOr.Add("(t.GenreRaw IS NULL OR TRIM(t.GenreRaw) = '')");
        if (f.Uncategorized)
            genreOr.Add("(t.GenreRaw IS NOT NULL AND TRIM(t.GenreRaw) <> '' AND NOT EXISTS (SELECT 1 FROM TrackGenres tg WHERE tg.TrackId = t.Id))");
        if (genreOr.Count > 0) where.Add("(" + string.Join(" OR ", genreOr) + ")");

        if (f.TagIds.Count > 0)
        {
            var names = new List<string>();
            for (var i = 0; i < f.TagIds.Count; i++)
            {
                names.Add($"@tag{i}");
                p.Add($"tag{i}", f.TagIds[i]);
            }
            where.Add($"t.Id IN (SELECT TrackId FROM TrackCustomTags WHERE TagId IN ({string.Join(", ", names)}))");
        }

        if (f.Decades.Count > 0)
        {
            var ors = new List<string>();
            for (var i = 0; i < f.Decades.Count; i++)
            {
                ors.Add($"(t.Year BETWEEN @decade{i} AND @decade{i} + 9)");
                p.Add($"decade{i}", f.Decades[i]);
            }
            where.Add("(" + string.Join(" OR ", ors) + ")");
        }
        if (f.Extensions.Count > 0)
        {
            var names = new List<string>();
            for (var i = 0; i < f.Extensions.Count; i++)
            {
                names.Add($"@ext{i}");
                p.Add($"ext{i}", "." + f.Extensions[i].TrimStart('.').ToLowerInvariant());
            }
            where.Add($"t.Extension IN ({string.Join(", ", names)})");
        }
        AddRange(where, p, "t.Bpm", "bpmF", f.BpmMin, f.BpmMax);
        AddRange(where, p, "t.Bpm", "bpmQ", parsed.BpmMin, parsed.BpmMax);
        AddRange(where, p, "t.Year", "yearQ", parsed.YearMin, parsed.YearMax);

        if (!string.IsNullOrWhiteSpace(f.MusicalKey)) { where.Add("t.MusicalKey = @keyF COLLATE NOCASE"); p.Add("keyF", f.MusicalKey.Trim()); }
        if (parsed.MusicalKey is not null) { where.Add("t.MusicalKey = @keyQ COLLATE NOCASE"); p.Add("keyQ", parsed.MusicalKey); }
        if (parsed.Extension is not null) { where.Add("t.Extension = @extQ"); p.Add("extQ", parsed.Extension); }

        foreach (var field in parsed.Missing.Distinct())
        {
            where.Add(field switch
            {
                "title" => "(t.Title IS NULL OR TRIM(t.Title) = '')",
                "artist" => "(t.Artist IS NULL OR TRIM(t.Artist) = '')",
                "album" => "(t.Album IS NULL OR TRIM(t.Album) = '')",
                "genre" => "(t.GenreRaw IS NULL OR TRIM(t.GenreRaw) = '')",
                "year" => "t.Year IS NULL",
                "bpm" => "(t.Bpm IS NULL OR t.Bpm <= 0)",
                "key" => "(t.MusicalKey IS NULL OR TRIM(t.MusicalKey) = '')",
                "art" or "cover" => "(t.ArtworkCacheKey IS NULL OR t.ArtworkCacheKey = '')",
                _ => "1 = 1"
            });
        }

        foreach (var issue in parsed.Issues.Distinct())
        {
            where.Add(issue switch
            {
                "mojibake" => $"(t.IssueFlags & {(int)IssueFlags.Mojibake}) <> 0",
                "error" => $"(t.IssueFlags & {(int)IssueFlags.Unreadable}) <> 0",
                "lowbitrate" => $"(t.IssueFlags & {(int)IssueFlags.LowBitrate}) <> 0",
                "samplerate" => $"(t.IssueFlags & {(int)IssueFlags.OddSampleRate}) <> 0",
                "mismatch" => $"(t.IssueFlags & {(int)IssueFlags.CodecMismatch}) <> 0",
                "size" => $"(t.IssueFlags & {(int)IssueFlags.SizeMismatch}) <> 0",
                "duration" => $"(t.IssueFlags & {(int)IssueFlags.ZeroDuration}) <> 0",
                _ => "t.IssueFlags <> 0"
            });
        }
        AddRange(where, p, "t.BitrateKbps", "kbpsQ", parsed.BitrateMin, parsed.BitrateMax);
        if (parsed.Duplicate is not null)
        {
            if (parsed.Duplicate == "exact")
                where.Add("EXISTS (SELECT 1 FROM FileHashes dh WHERE dh.TrackId = t.Id AND dh.FileSize = t.FileSize " +
                          "AND dh.Hash NOT IN (SELECT Hash FROM DuplicateDismissals) " +
                          "AND (SELECT COUNT(*) FROM FileHashes d2 JOIN Tracks t2 ON t2.Id = d2.TrackId AND t2.Missing = 0 AND t2.FileSize = d2.FileSize WHERE d2.Hash = dh.Hash) > 1)");
            else
            {
                where.Add("EXISTS (SELECT 1 FROM FileHashes dh WHERE dh.TrackId = t.Id AND dh.FileSize = t.FileSize AND dh.Hash LIKE @dupQ)");
                p.Add("dupQ", parsed.Duplicate + "%");
            }
        }
        if (parsed.Codec is not null) { where.Add("t.Codec LIKE @codecQ ESCAPE '\\'"); p.Add("codecQ", "%" + EscapeLike(parsed.Codec) + "%"); }

        if (q.SessionId is { } sid) p.Add("sid", sid);
        if (f.Played is { } pill) where.Add(PlayedClause(pill, q.SessionId));
        if (parsed.Played is { } typed) where.Add(PlayedClause(typed, q.SessionId));
        if (q.SessionId is not null)
        {
            if (f.HideConfirmedInSession) where.Add(InSession("Confirmed", negate: true));
            if (f.HidePossibleInSession) where.Add(InSession("Possible", negate: true));
        }

        if (where.Count == 0) where.Add("1 = 1");
        var cte = ctes.Count == 0 ? "" : "WITH RECURSIVE " + string.Join(", ", ctes) + " ";
        return (cte, where, p);
    }

    private static string InSession(string status, bool negate) =>
        $"{(negate ? "NOT " : "")}EXISTS (SELECT 1 FROM PlayHistory hp WHERE hp.TrackId = t.Id AND hp.SessionId = @sid " +
        $"AND hp.Status = '{status}' AND hp.SupersededByPlayId IS NULL)";

    /// <summary>"Played" filters read the maintained TrackPlayStats row; a track with no row has never been played.</summary>
    private static string PlayedClause(PlayedFilter filter, long? sessionId) => filter switch
    {
        PlayedFilter.Never => "ps.TrackId IS NULL",
        PlayedFilter.Possible => "ps.PossibleCount > 0",
        PlayedFilter.Confirmed => "ps.ConfirmedCount > 0",
        // Played in the live session: any non-superseded play there. No live session means nothing can match.
        PlayedFilter.Session => sessionId is null
            ? "1 = 0"
            : "EXISTS (SELECT 1 FROM PlayHistory hs WHERE hs.TrackId = t.Id AND hs.SessionId = @sid AND hs.SupersededByPlayId IS NULL)",
        _ => "1 = 1"
    };

    private static void AddRankParameters(DynamicParameters p, FtsMatchSet m)
    {
        if (m.Exact is not null) p.Add("mExact", m.Exact);
        if (m.ExactPrimary is not null) p.Add("mExactPrimary", m.ExactPrimary);
        if (m.Tier1 is not null) p.Add("mT1", m.Tier1);
        if (m.Tier2 is not null) p.Add("mT2", m.Tier2);
        if (m.Tier3 is not null) p.Add("mT3", m.Tier3);
        if (m.Primary is not null) p.Add("mPrimary", m.Primary);
    }

    private const string TitleKey = "COALESCE(NULLIF(TRIM(t.Title), ''), t.FileName)";

    /// <summary>
    /// "Best match first": a single text key (so keyset paging works exactly as for the other text sorts) made of a one-digit rank, a separator
    /// and the title. Rank = 2 x tier (0 as typed, 1 normalized, 2 other keyboard layout, 3 phonetic key, 4 typo) + 1 when the match is only
    /// in the path or genre rather than title/artist/album. Without text to match it is simply the title order.
    /// </summary>
    private static string RelevanceExpression(FtsMatchSet? m)
    {
        if (m is null || m.IsEmpty) return $"{TitleKey} COLLATE NOCASE";
        var (tier, notPrimary) = RankParts(m);
        return $"(printf('%d', ({tier}) * 2 + ({notPrimary})) || char(31) || {TitleKey}) COLLATE NOCASE";
    }

    /// <summary>
    /// The two halves of the rank as SQL: the tier (0 as typed, 1 normalized, 2 other keyboard layout, 3 phonetic key, 4 typo) and
    /// 0/1 for "the match is in title, artist or album" / "only in the path or genre".
    /// </summary>
    internal static (string Tier, string NotPrimary) RankParts(FtsMatchSet m)
    {
        static string InAlias(string param) => $"t.Id IN (SELECT rowid FROM TrackAliasSearch WHERE TrackAliasSearch MATCH @{param})";
        static string InRaw(string param) => $"t.Id IN (SELECT rowid FROM TrackSearch WHERE TrackSearch MATCH @{param})";

        var tier = new List<string>();
        if (m.Exact is not null) tier.Add($"WHEN {InRaw("mExact")} THEN 0");

        var tier1 = new List<string>();
        if (m.Tier1 is not null) tier1.Add(InAlias("mT1"));
        if (m.Legacy is not null) tier1.Add(InRaw("mLegacy"));
        if (tier1.Count > 0) tier.Add($"WHEN {string.Join(" OR ", tier1)} THEN 1");

        if (m.Tier2 is not null) tier.Add($"WHEN {InAlias("mT2")} THEN 2");
        if (m.Tier3 is not null) tier.Add($"WHEN {InAlias("mT3")} THEN 3");

        var tierExpr = tier.Count == 0 ? "4" : $"CASE {string.Join(" ", tier)} ELSE 4 END";

        var primary = new List<string>();
        if (m.Primary is not null) primary.Add(InAlias("mPrimary"));
        if (m.ExactPrimary is not null) primary.Add(InRaw("mExactPrimary"));
        var notPrimary = primary.Count == 0 ? "1" : $"CASE WHEN {string.Join(" OR ", primary)} THEN 0 ELSE 1 END";

        return (tierExpr, notPrimary);
    }

    /// <summary>The first <paramref name="limit"/> results in relevance order, each with its tier (for "Explain a search").</summary>
    public static (string Sql, DynamicParameters Parameters) BuildExplain(ParsedQuery parsed, FtsMatchSet match, int limit)
    {
        var q = new SearchQuery { GlobalScope = true, Sort = SortField.Relevance };
        var (cte, where, p) = BuildWhere(q, parsed, match, forPage: true);
        var (tier, notPrimary) = RankParts(match);
        p.Add("limit", limit);
        var sql = $"{cte}SELECT t.Id, {TitleKey} AS Title, t.Artist, ({tier}) AS Tier, ({notPrimary}) AS NotPrimary " +
                  $"FROM Tracks t WHERE {string.Join(" AND ", where)} ORDER BY {RelevanceExpression(match)} ASC, t.Id ASC LIMIT @limit";
        return (sql, p);
    }

    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static void AddRange(List<string> where, DynamicParameters p, string column, string name, double? min, double? max)
    {
        if (min is { } lo) { where.Add($"{column} >= @{name}Min"); p.Add($"{name}Min", lo); }
        if (max is { } hi) { where.Add($"{column} <= @{name}Max"); p.Add($"{name}Max", hi); }
    }

    /// <summary>
    /// The sort key never yields NULL so keyset comparisons stay simple; missing values sort last
    /// in both directions via a direction-dependent sentinel.
    /// </summary>
    private static (string Expr, bool Numeric) SortExpression(SortField field, SortDirection dir, FtsMatchSet? match)
    {
        var desc = dir == SortDirection.Desc;
        var textLast = desc ? "''" : "char(1114111)";
        var numLast = desc ? "-1" : "1e18";
        return field switch
        {
            SortField.Title => ("COALESCE(NULLIF(TRIM(t.Title), ''), t.FileName) COLLATE NOCASE", false),
            SortField.Artist => ($"COALESCE(NULLIF(TRIM(t.Artist), ''), {textLast}) COLLATE NOCASE", false),
            SortField.Key => ($"COALESCE(NULLIF(t.MusicalKey, ''), {textLast}) COLLATE NOCASE", false),
            SortField.Bpm => ($"COALESCE(t.Bpm, {numLast})", true),
            SortField.Duration => ($"COALESCE(t.DurationMs, {numLast})", true),
            SortField.FileDate => ("t.ModifiedUtc", false),
            SortField.LastPlayed => ($"COALESCE(ps.LastPlayedUtc, {textLast})", false),
            SortField.LastConfirmed => ($"COALESCE(ps.LastConfirmedUtc, {textLast})", false),
            SortField.LastPossible => ($"COALESCE(ps.LastPossibleUtc, {textLast})", false),
            SortField.PlayCount => ("COALESCE(ps.ConfirmedCount + ps.PossibleCount, 0)", true),
            SortField.Relevance => (RelevanceExpression(match), false),
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
    }
}
