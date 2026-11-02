using Dapper;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.Infrastructure.Persistence.Repositories;

public sealed class SimilarCandidateRepository : ISimilarCandidateSource
{
    /// <summary>On a huge library only this many candidates are scored, nearest tempo first. Deterministic, so the same query gives the same list.</summary>
    private const int CandidateCap = 20_000;

    private const string Columns = @"t.Id, COALESCE(NULLIF(TRIM(t.Title), ''), t.FileName) AS Title, t.Artist, t.Year, t.Bpm, t.MusicalKey,
        (SELECT group_concat(GenreId) FROM TrackGenres WHERE TrackId = t.Id) AS Genres,
        (SELECT group_concat(TagId) FROM TrackCustomTags WHERE TrackId = t.Id) AS Tags";

    private readonly SqliteConnectionFactory _factory;

    public SimilarCandidateRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task<SimilarSignals?> GetSeedAsync(long trackId, CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        var row = await conn.QuerySingleOrDefaultAsync<Row>(
            new CommandDefinition($"SELECT {Columns} FROM Tracks t WHERE t.Id = @trackId", new { trackId }, cancellationToken: ct));
        return row?.ToSignals();
    }

    public async Task<IReadOnlyList<SimilarSignals>> GetCandidatesAsync(SimilarSignals seed, SimilarTracksOptions options,
        IReadOnlyCollection<long> relatedGenreIds, CancellationToken ct = default)
    {
        var p = new DynamicParameters();
        p.Add("seedId", seed.TrackId);
        p.Add("seedBpm", seed.Bpm);
        p.Add("genres", relatedGenreIds.ToArray());
        p.Add("tags", seed.TagIds.ToArray());
        p.Add("cap", CandidateCap);

        // Any one of: tempo near the seed's (also at double and half time when allowed), a related genre, a shared DJ tag.
        var reasons = new List<string>();
        if (seed.Bpm is > 0)
        {
            var tempos = new List<double> { seed.Bpm.Value };
            if (options.AllowHalfDoubleTempo) { tempos.Add(seed.Bpm.Value * 2); tempos.Add(seed.Bpm.Value / 2); }
            for (var i = 0; i < tempos.Count; i++)
            {
                p.Add($"lo{i}", tempos[i] - options.BpmWindow);
                p.Add($"hi{i}", tempos[i] + options.BpmWindow);
                reasons.Add($"t.Bpm BETWEEN @lo{i} AND @hi{i}");
            }
        }
        if (relatedGenreIds.Count > 0) reasons.Add("t.Id IN (SELECT TrackId FROM TrackGenres WHERE GenreId IN @genres)");
        if (seed.TagIds.Count > 0) reasons.Add("t.Id IN (SELECT TrackId FROM TrackCustomTags WHERE TagId IN @tags)");
        if (reasons.Count == 0) return [];

        var where = new List<string> { "t.Missing = 0", "t.Id <> @seedId", "(" + string.Join(" OR ", reasons) + ")" };

        if (options.ExcludePlayedSinceUtc is { } since)
        {
            p.Add("since", DapperConfig.ToDb(since));
            where.Add("t.Id NOT IN (SELECT TrackId FROM TrackPlayStats WHERE LastPlayedUtc >= @since)");
        }
        if (options.ExcludeSessionId is { } session)
        {
            p.Add("sessionId", session);
            where.Add("NOT EXISTS (SELECT 1 FROM PlayHistory ph WHERE ph.TrackId = t.Id AND ph.SessionId = @sessionId AND ph.SupersededByPlayId IS NULL)");
        }

        var sql = $@"SELECT {Columns} FROM Tracks t WHERE {string.Join(" AND ", where)}
                     ORDER BY CASE WHEN @seedBpm IS NULL THEN 0 ELSE ABS(COALESCE(t.Bpm, 1e9) - @seedBpm) END, t.Id LIMIT @cap";

        using var conn = _factory.Open();
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(sql, p, cancellationToken: ct));
        return rows.Select(r => r.ToSignals()).ToList();
    }

    private sealed class Row
    {
        public long Id { get; set; }
        public string Title { get; set; } = "";
        public string? Artist { get; set; }
        public int? Year { get; set; }
        public double? Bpm { get; set; }
        public string? MusicalKey { get; set; }
        public string? Genres { get; set; }
        public string? Tags { get; set; }

        public SimilarSignals ToSignals() => new(Id, Title, Artist, Year, Bpm, MusicalKey, Ids(Genres), Ids(Tags));

        // The same genre can appear twice (embedded and user-assigned).
        private static IReadOnlyList<long> Ids(string? csv) =>
            string.IsNullOrEmpty(csv) ? [] : csv.Split(',').Select(long.Parse).Distinct().ToList();
    }
}
