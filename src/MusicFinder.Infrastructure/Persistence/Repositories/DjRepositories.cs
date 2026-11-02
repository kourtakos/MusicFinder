using Dapper;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Entities;
using MusicFinder.Domain.Enums;
using MusicFinder.Domain.Models;

namespace MusicFinder.Infrastructure.Persistence.Repositories;

public sealed class DjSessionRepository : IDjSessionRepository
{
    private const string Columns = "Id, StartedUtc, EndedUtc, DjSoftware, Name, AutoStarted";
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _writes;

    public DjSessionRepository(SqliteConnectionFactory factory, SqliteWriteQueue writes)
    {
        _factory = factory;
        _writes = writes;
    }

    public Task<long> StartAsync(DateTime startedUtc, string? djSoftware, string? name, bool autoStarted, CancellationToken ct = default) =>
        _writes.EnqueueAsync((conn, tx) => conn.QuerySingle<long>(
            "INSERT INTO DjSessions(StartedUtc, DjSoftware, Name, AutoStarted) VALUES (@startedUtc, @djSoftware, @name, @autoStarted) RETURNING Id",
            new { startedUtc = DapperConfig.ToDb(startedUtc), djSoftware, name, autoStarted }, tx), ct);

    public Task EndAsync(long id, DateTime endedUtc, CancellationToken ct = default) =>
        _writes.EnqueueAsync((conn, tx) =>
            conn.Execute("UPDATE DjSessions SET EndedUtc = @ended WHERE Id = @id AND EndedUtc IS NULL",
                new { id, ended = DapperConfig.ToDb(endedUtc) }, tx), ct);

    public Task UpdateAsync(long id, string? djSoftware, string? name, CancellationToken ct = default) =>
        _writes.EnqueueAsync((conn, tx) =>
            conn.Execute("UPDATE DjSessions SET DjSoftware = @djSoftware, Name = @name WHERE Id = @id", new { id, djSoftware, name }, tx), ct);

    public async Task<DjSession?> GetAsync(long id, CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        return await conn.QuerySingleOrDefaultAsync<DjSession>($"SELECT {Columns} FROM DjSessions WHERE Id = @id", new { id });
    }

    public async Task<DjSession?> GetActiveAsync(CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        return await conn.QueryFirstOrDefaultAsync<DjSession>(
            $"SELECT {Columns} FROM DjSessions WHERE EndedUtc IS NULL ORDER BY Id DESC LIMIT 1");
    }

    public async Task<IReadOnlyList<DjSession>> GetRecentAsync(int limit, CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        return (await conn.QueryAsync<DjSession>($"SELECT {Columns} FROM DjSessions ORDER BY Id DESC LIMIT @limit", new { limit })).AsList();
    }

    public async Task<DateTime?> GetLastEventUtcAsync(long sessionId, CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        var text = await conn.ExecuteScalarAsync<string?>("SELECT MAX(OccurredUtc) FROM TrackEvents WHERE SessionId = @sessionId", new { sessionId });
        return text is null ? null : DapperConfig.FromDb(text);
    }
}

public sealed class TrackEventRepository : ITrackEventRepository
{
    private const string Columns = "Id, TrackId, SessionId, EventType, OccurredUtc, Source, Confidence, EvidenceJson";
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _writes;

    public TrackEventRepository(SqliteConnectionFactory factory, SqliteWriteQueue writes)
    {
        _factory = factory;
        _writes = writes;
    }

    public Task<IReadOnlyList<long>> AddAsync(IReadOnlyList<TrackEvent> events, CancellationToken ct = default) =>
        events.Count == 0
            ? Task.FromResult<IReadOnlyList<long>>([])
            : _writes.EnqueueAsync<IReadOnlyList<long>>((conn, tx) =>
            {
                var ids = new List<long>(events.Count);
                foreach (var e in events)
                    ids.Add(conn.QuerySingle<long>(
                        """
                        INSERT INTO TrackEvents(TrackId, SessionId, EventType, OccurredUtc, Source, Confidence, EvidenceJson)
                        VALUES (@TrackId, @SessionId, @EventType, @OccurredUtc, @Source, @Confidence, @EvidenceJson) RETURNING Id
                        """,
                        new { e.TrackId, e.SessionId, e.EventType, OccurredUtc = DapperConfig.ToDb(e.OccurredUtc), e.Source, e.Confidence, e.EvidenceJson }, tx));
                return ids;
            }, ct);

    public async Task<TrackEvent?> GetAsync(long id, CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        return await conn.QuerySingleOrDefaultAsync<TrackEvent>($"SELECT {Columns} FROM TrackEvents WHERE Id = @id", new { id });
    }

    public async Task<IReadOnlyList<TrackEvent>> GetForSessionAsync(long sessionId, CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        return (await conn.QueryAsync<TrackEvent>(
            $"SELECT {Columns} FROM TrackEvents WHERE SessionId = @sessionId ORDER BY Id", new { sessionId })).AsList();
    }

    public async Task<IReadOnlyList<TrackEvent>> GetForTrackAsync(long trackId, int limit, CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        return (await conn.QueryAsync<TrackEvent>(
            $"SELECT {Columns} FROM TrackEvents WHERE TrackId = @trackId ORDER BY OccurredUtc DESC, Id DESC LIMIT @limit",
            new { trackId, limit })).AsList();
    }

    public async Task<IReadOnlyList<TrackEvent>> GetAnchorsForTrackAsync(long sessionId, long trackId, CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        return (await conn.QueryAsync<TrackEvent>(
            $"SELECT {Columns} FROM TrackEvents WHERE SessionId = @sessionId AND TrackId = @trackId AND EventType = 'Exported' ORDER BY Id DESC",
            new { sessionId, trackId })).AsList();
    }
}

public sealed class PlayHistoryRepository : IPlayHistoryRepository
{
    private const string Columns = "Id, TrackId, SessionId, PlayedUtc, Status, Source, ExternalEventKey, RelatedEventId, SupersededByPlayId";
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _writes;

    public PlayHistoryRepository(SqliteConnectionFactory factory, SqliteWriteQueue writes)
    {
        _factory = factory;
        _writes = writes;
    }

    private static object Params(PlayRecord p) => new
    {
        p.Id, p.TrackId, p.SessionId, PlayedUtc = DapperConfig.ToDb(p.PlayedUtc), p.Status, p.Source, p.ExternalEventKey,
        p.RelatedEventId, p.SupersededByPlayId
    };

    // "ON CONFLICT DO NOTHING" ignores only duplicate keys. "INSERT OR IGNORE" would also swallow a CHECK violation (an
    // invalid status) and would override the conflict clauses inside the statistics triggers.
    public Task<long?> AddAsync(PlayRecord play, CancellationToken ct = default) =>
        _writes.EnqueueAsync((conn, tx) => conn.QuerySingleOrDefault<long?>(
            """
            INSERT INTO PlayHistory(TrackId, SessionId, PlayedUtc, Status, Source, ExternalEventKey, RelatedEventId, SupersededByPlayId)
            VALUES (@TrackId, @SessionId, @PlayedUtc, @Status, @Source, @ExternalEventKey, @RelatedEventId, @SupersededByPlayId)
            ON CONFLICT DO NOTHING RETURNING Id
            """, Params(play), tx), ct);

    // Only plays MusicFinder itself created (inferred or manual) can be removed by the user's "not played" decision.
    private const string Removable = "Source IN ('InactivityHeuristic', 'User')";

    public Task<IReadOnlyList<PlayRecord>> DeleteByAnchorAsync(long anchorEventId, CancellationToken ct = default) =>
        _writes.EnqueueAsync<IReadOnlyList<PlayRecord>>((conn, tx) =>
        {
            var rows = conn.Query<PlayRecord>(
                $"SELECT {Columns} FROM PlayHistory WHERE RelatedEventId = @anchorEventId AND {Removable}", new { anchorEventId }, tx).AsList();
            conn.Execute($"DELETE FROM PlayHistory WHERE RelatedEventId = @anchorEventId AND {Removable}", new { anchorEventId }, tx);
            return rows;
        }, ct);

    public Task<IReadOnlyList<PlayRecord>> DeleteLatestRemovableAsync(long trackId, CancellationToken ct = default) =>
        _writes.EnqueueAsync<IReadOnlyList<PlayRecord>>((conn, tx) =>
        {
            var rows = conn.Query<PlayRecord>(
                $"SELECT {Columns} FROM PlayHistory WHERE TrackId = @trackId AND SupersededByPlayId IS NULL AND {Removable} ORDER BY PlayedUtc DESC, Id DESC LIMIT 1",
                new { trackId }, tx).AsList();
            foreach (var row in rows) conn.Execute("DELETE FROM PlayHistory WHERE Id = @Id", new { row.Id }, tx);
            return rows;
        }, ct);

    public Task DeleteAsync(long playId, CancellationToken ct = default) =>
        _writes.EnqueueAsync((conn, tx) => conn.Execute("DELETE FROM PlayHistory WHERE Id = @playId", new { playId }, tx), ct);

    public Task RestoreAsync(PlayRecord play, CancellationToken ct = default) =>
        _writes.EnqueueAsync((conn, tx) => conn.Execute(
            """
            INSERT INTO PlayHistory(Id, TrackId, SessionId, PlayedUtc, Status, Source, ExternalEventKey, RelatedEventId, SupersededByPlayId)
            VALUES (@Id, @TrackId, @SessionId, @PlayedUtc, @Status, @Source, @ExternalEventKey, @RelatedEventId, @SupersededByPlayId)
            ON CONFLICT DO NOTHING
            """, Params(play), tx), ct);

    public Task MarkSupersededAsync(long possiblePlayId, long confirmedPlayId, CancellationToken ct = default) =>
        _writes.EnqueueAsync((conn, tx) => conn.Execute(
            "UPDATE PlayHistory SET SupersededByPlayId = @confirmedPlayId WHERE Id = @possiblePlayId",
            new { possiblePlayId, confirmedPlayId }, tx), ct);

    public async Task<IReadOnlyList<PlayHistoryItem>> GetForTrackAsync(long trackId, int limit, CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        var rows = await conn.QueryAsync<(long Id, DateTime PlayedUtc, string Status, string Source, long? SessionId, string? SessionName, long? SupersededBy)>(
            """
            SELECT p.Id, p.PlayedUtc, p.Status, p.Source, p.SessionId, s.Name, p.SupersededByPlayId
            FROM PlayHistory p LEFT JOIN DjSessions s ON s.Id = p.SessionId
            WHERE p.TrackId = @trackId ORDER BY p.PlayedUtc DESC, p.Id DESC LIMIT @limit
            """, new { trackId, limit });
        return rows.Select(r => new PlayHistoryItem(
            r.Id, UtcTime.Normalize(r.PlayedUtc), Enum.Parse<PlayStatus>(r.Status), r.Source, r.SessionId, r.SessionName,
            r.SupersededBy is not null)).ToList();
    }

    public async Task<IReadOnlyList<SessionEntryRow>> GetSessionEntriesAsync(long sessionId, CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        // One row per Exported event: its track, the play it produced (if any) and the latest thing that resolved it
        // (an interruption or a manual decision), found through the anchor id each of those events records.
        var rows = await conn.QueryAsync<Row>(
            """
            SELECT e.Id AS AnchorEventId, e.TrackId, t.Title, t.Artist, t.FileName, e.OccurredUtc AS ExportedUtc,
                   e.EvidenceJson AS ExportEvidenceJson,
                   p.Id AS PlayId, p.Status AS PlayStatus, p.Source AS PlaySource, p.PlayedUtc,
                   r.EventType AS ResolutionEventType, r.EvidenceJson AS ResolutionEvidenceJson
            FROM TrackEvents e
            JOIN Tracks t ON t.Id = e.TrackId
            LEFT JOIN PlayHistory p ON p.RelatedEventId = e.Id AND p.SupersededByPlayId IS NULL
            LEFT JOIN TrackEvents r ON r.Id = (
                SELECT r2.Id FROM TrackEvents r2
                WHERE r2.SessionId = e.SessionId AND r2.TrackId = e.TrackId
                  AND r2.EventType IN ('ActivityInterrupted', 'ManualMarkPlayed', 'ManualMarkNotPlayed')
                  AND json_extract(r2.EvidenceJson, '$.anchorEventId') = e.Id
                ORDER BY r2.Id DESC LIMIT 1)
            WHERE e.SessionId = @sessionId AND e.EventType = 'Exported'
            ORDER BY e.OccurredUtc DESC, e.Id DESC
            """, new { sessionId });
        return rows.Select(r => new SessionEntryRow(
            r.AnchorEventId, r.TrackId, string.IsNullOrWhiteSpace(r.Title) ? System.IO.Path.GetFileNameWithoutExtension(r.FileName) : r.Title,
            r.Artist, r.FileName, UtcTime.Normalize(r.ExportedUtc), r.ExportEvidenceJson,
            r.PlayId, r.PlayStatus, r.PlaySource, UtcTime.Normalize(r.PlayedUtc),
            r.ResolutionEventType, r.ResolutionEvidenceJson)).ToList();
    }

    private sealed class Row
    {
        public long AnchorEventId { get; set; }
        public long TrackId { get; set; }
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string FileName { get; set; } = "";
        public DateTime ExportedUtc { get; set; }
        public string? ExportEvidenceJson { get; set; }
        public long? PlayId { get; set; }
        public string? PlayStatus { get; set; }
        public string? PlaySource { get; set; }
        public DateTime? PlayedUtc { get; set; }
        public string? ResolutionEventType { get; set; }
        public string? ResolutionEvidenceJson { get; set; }
    }
}
