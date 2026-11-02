using Dapper;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.Infrastructure.Persistence.Repositories;

/// <summary>Tracks for the probable-duplicate search (library data only) and the "not a duplicate" decisions.</summary>
public sealed class SongCandidateRepository : ISongCandidateStore
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _writes;
    private readonly TimeProvider _time;

    public SongCandidateRepository(SqliteConnectionFactory factory, SqliteWriteQueue writes, TimeProvider? time = null)
    {
        _factory = factory;
        _writes = writes;
        _time = time ?? TimeProvider.System;
    }

    // A class with settable properties: with no rows Dapper cannot match a positional constructor to expression columns.
    private sealed class Row
    {
        public long Id { get; set; }
        public string FullPath { get; set; } = "";
        public string Extension { get; set; } = "";
        public string? Artist { get; set; }
        public string? Title { get; set; }
        public long? DurationMs { get; set; }
        public long? BitrateKbps { get; set; }
        public string? Codec { get; set; }
        public long? Confirmed { get; set; }
        public long? Possible { get; set; }
    }

    public async Task<IReadOnlyList<SongCandidate>> GetCandidatesAsync(CancellationToken ct)
    {
        using var conn = _factory.Open();
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(
            """
            SELECT t.Id, t.FullPath, t.Extension, t.Artist, t.Title, t.DurationMs, t.BitrateKbps, t.Codec,
                   ps.ConfirmedCount AS Confirmed, ps.PossibleCount AS Possible
            FROM Tracks t LEFT JOIN TrackPlayStats ps ON ps.TrackId = t.Id
            WHERE t.Missing = 0 AND t.Artist IS NOT NULL AND t.Title IS NOT NULL AND TRIM(t.Artist) <> '' AND TRIM(t.Title) <> ''
            ORDER BY t.Id
            """, cancellationToken: ct));
        return rows.Select(r => new SongCandidate(r.Id, r.FullPath, r.Extension, r.Artist, r.Title, (int?)r.DurationMs, (int?)r.BitrateKbps,
            r.Codec, (int)((r.Confirmed ?? 0) + (r.Possible ?? 0)))).ToList();
    }

    public async Task<IReadOnlySet<string>> GetDismissedKeysAsync(CancellationToken ct)
    {
        using var conn = _factory.Open();
        var keys = await conn.QueryAsync<string>(new CommandDefinition("SELECT Key FROM ProbableDismissals", cancellationToken: ct));
        return keys.ToHashSet(StringComparer.Ordinal);
    }

    public Task DismissAsync(string key, CancellationToken ct) =>
        _writes.EnqueueAsync((conn, tx) => conn.Execute(
            "INSERT OR IGNORE INTO ProbableDismissals(Key, DismissedUtc) VALUES (@key, @now)",
            new { key, now = DapperConfig.ToDb(_time.GetUtcNow().UtcDateTime) }, tx), ct);
}
