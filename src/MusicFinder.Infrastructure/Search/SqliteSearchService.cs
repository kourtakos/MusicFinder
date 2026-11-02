using System.Globalization;
using Dapper;
using MusicFinder.Application.Search;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Enums;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure.Persistence;

namespace MusicFinder.Infrastructure.Search;

public sealed class SqliteSearchService : ISearchService, ISearchDiagnostics
{
    private readonly SqliteConnectionFactory _factory;
    private readonly ISettingsStore? _settings;
    private readonly IVocabularyLookup? _vocabulary;
    private ExpansionOptions _options = new();
    private long _optionsLoadedAt;

    /// <param name="settings">Optional: supplies the Smart Greek and typo-matching switches (both on when absent).</param>
    /// <param name="vocabulary">Optional: real words of the library, for typo candidates.</param>
    public SqliteSearchService(SqliteConnectionFactory factory, ISettingsStore? settings = null, IVocabularyLookup? vocabulary = null)
    {
        _factory = factory;
        _settings = settings;
        _vocabulary = vocabulary;
    }

    /// <summary>The expansion switches, re-read from settings at most once a second (typing must not hit the disk every keystroke).</summary>
    private ExpansionOptions CurrentOptions()
    {
        if (_settings is null) return _options;
        var now = Environment.TickCount64;
        if (now - Volatile.Read(ref _optionsLoadedAt) > 1000 || _optionsLoadedAt == 0)
        {
            var s = _settings.Load();
            _options = new ExpansionOptions { SmartGreek = s.SmartGreekSearch, Fuzzy = s.FuzzySearch };
            Volatile.Write(ref _optionsLoadedAt, now);
        }
        return _options;
    }

    private bool _indexComplete;
    private long _indexCheckedAt;

    /// <summary>
    /// True when every track has an up-to-date alias row. Then the original index is not consulted at all (it only exists to keep tracks
    /// searchable while their alias rows are being built), which removes two sets from every query. Re-checked at most every two seconds.
    /// </summary>
    private bool IndexComplete()
    {
        var now = Environment.TickCount64;
        if (_indexCheckedAt != 0 && now - Volatile.Read(ref _indexCheckedAt) < 2000) return _indexComplete;
        try
        {
            using var conn = _factory.Open();
            _indexComplete = conn.ExecuteScalar<long>(
                "SELECT EXISTS (SELECT 1 FROM Tracks WHERE SearchAliasVersion < @version)", new { version = SearchTextBuilder.CurrentVersion }) == 0;
        }
        catch (Exception) { _indexComplete = false; } // when in doubt keep the original index in the query
        Volatile.Write(ref _indexCheckedAt, now);
        return _indexComplete;
    }

    /// <summary>The MATCH expressions for what was typed. With Smart Greek off, only the original index is used (the pre-M7 behaviour).</summary>
    public FtsMatchSet BuildMatch(ParsedQuery parsed)
    {
        var options = CurrentOptions();
        var set = FtsMatchBuilder.Build(QueryExpander.Expand(parsed, options, _vocabulary));
        if (!options.SmartGreek) return set with { All = null, Tier1 = null, Tier2 = null, Tier3 = null, Primary = null };
        // With a complete alias index the original one is redundant: the alias forms already include what was typed.
        return IndexComplete() ? set with { Legacy = null, ExactPrimary = null } : set;
    }

    public async Task<SearchPage> SearchAsync(SearchQuery query, CancellationToken ct)
    {
        var parsed = SearchQueryParser.Parse(query.Text);
        var (sql, parameters) = SearchSqlBuilder.BuildPage(query, parsed, BuildMatch(parsed));

        using var conn = _factory.Open();

        // Step 1: which tracks, in which order (ids and sort keys only).
        var keys = (await conn.QueryAsync<PageKey>(new CommandDefinition(sql, parameters, cancellationToken: ct))).AsList();
        ct.ThrowIfCancellationRequested();

        var pageSize = Math.Clamp(query.PageSize, 1, 1000);
        var hasMore = keys.Count > pageSize;
        if (hasMore) keys.RemoveAt(keys.Count - 1);

        // Step 2: the full rows of just those tracks, put back in order.
        var items = new List<SearchResultRow>(keys.Count);
        if (keys.Count > 0)
        {
            var ids = keys.Select(k => k.Id).ToList();
            var rows = (await conn.QueryAsync<Row>(new CommandDefinition(
                SearchSqlBuilder.BuildByIds(query.SessionId), new { ids, sid = query.SessionId }, cancellationToken: ct))).ToDictionary(r => r.Id);
            ct.ThrowIfCancellationRequested();
            foreach (var key in keys)
                if (rows.TryGetValue(key.Id, out var row)) items.Add(ToResult(row)); // a track deleted between the two steps is simply skipped
        }

        SearchCursor? next = keys.Count > 0 && hasMore
            ? new SearchCursor(Convert.ToString(keys[^1].SortKey, CultureInfo.InvariantCulture), keys[^1].Id)
            : null;
        return new SearchPage(items, next, hasMore, parsed.Warnings);
    }

    private sealed class PageKey
    {
        public long Id { get; set; }
        public object? SortKey { get; set; }
    }

    /// <summary>How the query is read and why each of the first results matched. Uses exactly the same expansion and SQL as a normal search.</summary>
    public async Task<SearchExplanation> ExplainAsync(string text, CancellationToken ct = default)
    {
        const int Limit = 50;
        var parsed = SearchQueryParser.Parse(text);
        var options = CurrentOptions();
        var expanded = QueryExpander.Expand(parsed, options, _vocabulary);
        var match = BuildMatch(parsed);

        var terms = expanded.Terms.Select(t =>
        {
            var forms = new List<ExplainedForm> { new("as typed", t.Raw), new("normalized", t.N) };
            if (t.KB is not null) forms.Add(new ExplainedForm("other keyboard layout", t.KB));
            if (t.K is not null) forms.Add(new ExplainedForm("phonetic key", t.K));
            if (t.KBK is not null) forms.Add(new ExplainedForm("phonetic key of the other layout", t.KBK));
            forms.AddRange(t.Fuzzy.Select(f => new ExplainedForm("typo candidate", f)));
            return new ExplainedTerm(t.Field, t.IsPhrase, forms);
        }).ToList();

        var results = new List<ExplainedResult>();
        if (!match.IsEmpty)
        {
            var (sql, parameters) = SearchSqlBuilder.BuildExplain(parsed, match, Limit);
            using var conn = _factory.Open();
            var rows = await conn.QueryAsync<(long Id, string Title, string? Artist, int Tier, int NotPrimary)>(
                new CommandDefinition(sql, parameters, cancellationToken: ct));
            results.AddRange(rows.Select(r => new ExplainedResult(r.Id, r.Title, r.Artist, r.Tier, r.NotPrimary == 0)));
        }
        return new SearchExplanation(text, parsed.Warnings, terms, results, Limit);
    }

    public async Task<IReadOnlyList<SearchResultRow>> GetRowsAsync(IReadOnlyList<long> trackIds, long? sessionId, CancellationToken ct)
    {
        if (trackIds.Count == 0) return [];
        using var conn = _factory.Open();
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(
            SearchSqlBuilder.BuildByIds(sessionId), new { ids = trackIds, sid = sessionId }, cancellationToken: ct));
        return rows.Select(ToResult).ToList();
    }

    public async Task<int> CountAsync(SearchQuery query, CancellationToken ct)
    {
        var parsed = SearchQueryParser.Parse(query.Text);
        var (sql, parameters) = SearchSqlBuilder.BuildCount(query with { After = null }, parsed, BuildMatch(parsed));
        using var conn = _factory.Open();
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(sql, parameters, cancellationToken: ct));
    }

    private static SearchResultRow ToResult(Row r) => new(
        r.Id, r.FolderId, r.FullPath, r.FileName, r.Extension, r.FileSize,
        string.IsNullOrWhiteSpace(r.Title) ? Path.GetFileNameWithoutExtension(r.FileName) : r.Title,
        r.Artist, r.Album, r.GenreRaw, r.Year, r.DurationMs, r.BitrateKbps, r.Bpm, r.MusicalKey, r.Missing,
        r.ArtworkCacheKey, SplitNames(r.GenreNames), SplitNames(r.TagNames),
        ParseUtc(r.LastPlayedUtc), ParseUtc(r.LastConfirmedUtc), ParseUtc(r.LastPossibleUtc),
        r.ConfirmedCount, r.PossibleCount, (SessionPlayStatus)r.SessionStatus,
        r.Codec, r.SampleRateHz, r.Channels, r.BitDepth, r.IssueFlags, r.ReadError);

    private static DateTime? ParseUtc(string? text) => string.IsNullOrEmpty(text) ? null : DapperConfig.FromDb(text);

    /// <summary>group_concat results use the ASCII unit separator, which cannot appear in names.</summary>
    private static IReadOnlyList<string>? SplitNames(string? joined) =>
        string.IsNullOrEmpty(joined) ? null : joined.Split('\u001f');

    private sealed class Row
    {
        public long Id { get; set; }
        public long FolderId { get; set; }
        public string FullPath { get; set; } = "";
        public string FileName { get; set; } = "";
        public string Extension { get; set; } = "";
        public long FileSize { get; set; }
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Album { get; set; }
        public string? GenreRaw { get; set; }
        public int? Year { get; set; }
        public long? DurationMs { get; set; }
        public int? BitrateKbps { get; set; }
        public double? Bpm { get; set; }
        public string? MusicalKey { get; set; }
        public bool Missing { get; set; }
        public string? ArtworkCacheKey { get; set; }
        public string? Codec { get; set; }
        public int? SampleRateHz { get; set; }
        public int? Channels { get; set; }
        public int? BitDepth { get; set; }
        public int IssueFlags { get; set; }
        public string? ReadError { get; set; }
        public string? GenreNames { get; set; }
        public string? TagNames { get; set; }
        public string? LastPlayedUtc { get; set; }
        public string? LastConfirmedUtc { get; set; }
        public string? LastPossibleUtc { get; set; }
        public int ConfirmedCount { get; set; }
        public int PossibleCount { get; set; }
        public int SessionStatus { get; set; }
        public object? SortKey { get; set; }
    }
}
