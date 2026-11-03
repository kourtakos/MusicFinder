using Dapper;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.Infrastructure.Persistence.Repositories;

/// <summary>What the history importers remember (files read, plays that found no track) and the lookups that tie a reported play to a library track.</summary>
public sealed class HistoryImportRepository : IHistoryImportStore, IHistoryTrackLookup
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _writes;

    public HistoryImportRepository(SqliteConnectionFactory factory, SqliteWriteQueue writes)
    {
        _factory = factory;
        _writes = writes;
    }

    // ===== import state =====

    public async Task<IReadOnlyDictionary<string, HistoryFileState>> GetFileStatesAsync(string source, CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        var rows = await conn.QueryAsync<(string FilePath, long Length, string LastWriteUtc)>(
            "SELECT FilePath, Length, LastWriteUtc FROM HistoryImportFiles WHERE Source = @source", new { source });
        return rows.ToDictionary(r => r.FilePath, r => new HistoryFileState(r.FilePath, r.Length, DapperConfig.FromDb(r.LastWriteUtc)), StringComparer.OrdinalIgnoreCase);
    }

    public Task SaveFileStateAsync(string source, HistoryFileState state, CancellationToken ct = default) =>
        _writes.EnqueueAsync((conn, tx) => conn.Execute(
            """
            INSERT INTO HistoryImportFiles(Source, FilePath, Length, LastWriteUtc, ImportedUtc)
            VALUES (@source, @FilePath, @Length, @write, @now)
            ON CONFLICT(Source, FilePath) DO UPDATE SET Length = excluded.Length, LastWriteUtc = excluded.LastWriteUtc, ImportedUtc = excluded.ImportedUtc
            """,
            new { source, state.FilePath, state.Length, write = DapperConfig.ToDb(state.LastWriteUtc), now = DapperConfig.ToDb(DateTime.UtcNow) }, tx), ct);

    public Task<bool> AddUnmatchedAsync(UnmatchedHistoryEvent e, CancellationToken ct = default) =>
        _writes.EnqueueAsync((conn, tx) => conn.Execute(
            """
            INSERT INTO HistoryImportUnmatched(Source, ExternalEventKey, PlayedUtc, SourcePath, Artist, Title, FileSize, DurationSeconds, Reason, CandidatesJson)
            VALUES (@Source, @ExternalEventKey, @played, @SourcePath, @Artist, @Title, @FileSize, @DurationSeconds, @Reason, @CandidatesJson)
            ON CONFLICT DO NOTHING
            """,
            new
            {
                e.Source, e.ExternalEventKey, played = DapperConfig.ToDb(e.PlayedUtc), e.SourcePath, e.Artist, e.Title, e.FileSize,
                e.DurationSeconds, e.Reason, e.CandidatesJson
            }, tx) > 0, ct);

    public async Task<IReadOnlyList<UnmatchedHistoryEvent>> GetUnmatchedAsync(string status, CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        var rows = await conn.QueryAsync<Row>(
            """
            SELECT Id, Source, ExternalEventKey, PlayedUtc, SourcePath, Artist, Title, FileSize, DurationSeconds, Reason, CandidatesJson, Status, ResolvedTrackId
            FROM HistoryImportUnmatched WHERE Status = @status ORDER BY PlayedUtc, Id
            """, new { status });
        return rows.Select(r => new UnmatchedHistoryEvent(
            r.Id, r.Source, r.ExternalEventKey, UtcTime.Normalize(r.PlayedUtc), r.SourcePath, r.Artist, r.Title, r.FileSize, r.DurationSeconds,
            r.Reason, r.CandidatesJson, r.Status, r.ResolvedTrackId)).ToList();
    }

    public async Task<int> CountPendingAsync(CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        return await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM HistoryImportUnmatched WHERE Status = 'Pending'");
    }

    public Task SetUnmatchedStatusAsync(long id, string status, long? trackId, CancellationToken ct = default) =>
        _writes.EnqueueAsync((conn, tx) => conn.Execute(
            "UPDATE HistoryImportUnmatched SET Status = @status, ResolvedTrackId = @trackId, ResolvedUtc = @now WHERE Id = @id",
            new { id, status, trackId, now = DapperConfig.ToDb(DateTime.UtcNow) }, tx), ct);

    // ===== lookups =====

    public async Task<IReadOnlyList<long>> FindByPathAsync(string path, CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        return (await conn.QueryAsync<long>("SELECT Id FROM Tracks WHERE FullPath = @path COLLATE NOCASE ORDER BY Id", new { path })).AsList();
    }

    public async Task<IReadOnlyList<long>> FindByNameAndSizeAsync(string fileName, long size, CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        return (await conn.QueryAsync<long>(
            "SELECT Id FROM Tracks WHERE FileName = @fileName COLLATE NOCASE AND FileSize = @size ORDER BY Id", new { fileName, size })).AsList();
    }

    public async Task<IReadOnlyList<long>> FindByNameAndDurationAsync(string fileName, double seconds, double toleranceSeconds, CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        return (await conn.QueryAsync<long>(
            """
            SELECT Id FROM Tracks
            WHERE FileName = @fileName COLLATE NOCASE AND DurationMs IS NOT NULL AND ABS(DurationMs - @ms) <= @tolerance ORDER BY Id
            """, new { fileName, ms = seconds * 1000.0, tolerance = toleranceSeconds * 1000.0 })).AsList();
    }

    private sealed class Row
    {
        public long Id { get; set; }
        public string Source { get; set; } = "";
        public string ExternalEventKey { get; set; } = "";
        public DateTime PlayedUtc { get; set; }
        public string SourcePath { get; set; } = "";
        public string? Artist { get; set; }
        public string? Title { get; set; }
        public long? FileSize { get; set; }
        public double? DurationSeconds { get; set; }
        public string Reason { get; set; } = "";
        public string? CandidatesJson { get; set; }
        public string Status { get; set; } = "";
        public long? ResolvedTrackId { get; set; }
    }
}
