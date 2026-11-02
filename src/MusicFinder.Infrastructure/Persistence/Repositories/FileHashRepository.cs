using Dapper;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.Infrastructure.Persistence.Repositories;

/// <summary>
/// Remembered SHA-256 hashes. A row exists only for the bytes the track has now: the database drops it when the track's
/// size or modified time changes (trigger Tracks_au_hashStale in migration 0010).
/// </summary>
public sealed class FileHashRepository : IFileHashStore
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _writes;
    private readonly TimeProvider _time;

    public FileHashRepository(SqliteConnectionFactory factory, SqliteWriteQueue writes, TimeProvider? time = null)
    {
        _factory = factory;
        _writes = writes;
        _time = time ?? TimeProvider.System;
    }

    private sealed record CandidateRow(long Id, string FullPath, long FileSize, string ModifiedUtc, string? Hash);

    public async Task<IReadOnlyList<HashCandidate>> GetCandidatesAsync(CancellationToken ct)
    {
        using var conn = _factory.Open();
        var rows = await conn.QueryAsync<CandidateRow>(new CommandDefinition(
            """
            SELECT t.Id, t.FullPath, t.FileSize, t.ModifiedUtc, h.Hash
            FROM Tracks t LEFT JOIN FileHashes h ON h.TrackId = t.Id AND h.FileSize = t.FileSize
            WHERE t.Missing = 0 AND t.FileSize > 0
              AND t.FileSize IN (SELECT FileSize FROM Tracks WHERE Missing = 0 GROUP BY FileSize HAVING COUNT(*) > 1)
            ORDER BY t.FileSize, t.Id
            """, cancellationToken: ct));
        return rows.Select(r => new HashCandidate(r.Id, r.FullPath, r.FileSize, DapperConfig.FromDb(r.ModifiedUtc), r.Hash)).ToList();
    }

    public Task PutAsync(long trackId, long fileSize, DateTime modifiedUtc, string hash, CancellationToken ct) =>
        _writes.EnqueueAsync((conn, tx) => conn.Execute(
            """
            INSERT INTO FileHashes(TrackId, FileSize, ModifiedUtc, Algorithm, Hash, ComputedUtc)
            VALUES (@trackId, @fileSize, @modified, 'SHA256', @hash, @now)
            ON CONFLICT(TrackId) DO UPDATE SET FileSize = excluded.FileSize, ModifiedUtc = excluded.ModifiedUtc,
                Hash = excluded.Hash, ComputedUtc = excluded.ComputedUtc
            """,
            new { trackId, fileSize, modified = DapperConfig.ToDb(modifiedUtc), hash, now = DapperConfig.ToDb(_time.GetUtcNow().UtcDateTime) }, tx), ct);

    private sealed record MemberRow(
        string Hash, long Id, string FullPath, string Extension, long FileSize, string? Artist, string? Title,
        long? BitrateKbps, string? Codec, long? Confirmed, long? Possible, string ModifiedUtc);

    public async Task<IReadOnlyList<HashedMember>> GetHashedMembersAsync(CancellationToken ct)
    {
        using var conn = _factory.Open();
        var rows = await conn.QueryAsync<MemberRow>(new CommandDefinition(
            """
            SELECT h.Hash, t.Id, t.FullPath, t.Extension, t.FileSize, t.Artist, t.Title, t.BitrateKbps, t.Codec,
                   ps.ConfirmedCount AS Confirmed, ps.PossibleCount AS Possible, t.ModifiedUtc
            FROM FileHashes h
            JOIN Tracks t ON t.Id = h.TrackId AND t.Missing = 0 AND t.FileSize = h.FileSize
            LEFT JOIN TrackPlayStats ps ON ps.TrackId = t.Id
            WHERE h.Hash IN (SELECT h2.Hash FROM FileHashes h2 JOIN Tracks t2 ON t2.Id = h2.TrackId AND t2.Missing = 0 AND t2.FileSize = h2.FileSize
                             GROUP BY h2.Hash HAVING COUNT(*) > 1)
              AND h.Hash NOT IN (SELECT Hash FROM DuplicateDismissals)
            ORDER BY h.Hash, t.FullPath
            """, cancellationToken: ct));
        return rows.Select(r => new HashedMember(r.Hash, new DuplicateMember(
            r.Id, r.FullPath, r.Extension, r.FileSize, r.Artist, r.Title, (int?)r.BitrateKbps, r.Codec, (int)((r.Confirmed ?? 0) + (r.Possible ?? 0)), DapperConfig.FromDb(r.ModifiedUtc)))).ToList();
    }

    public Task DismissAsync(string hash, CancellationToken ct) =>
        _writes.EnqueueAsync((conn, tx) => conn.Execute(
            "INSERT OR IGNORE INTO DuplicateDismissals(Hash, DismissedUtc) VALUES (@hash, @now)",
            new { hash, now = DapperConfig.ToDb(_time.GetUtcNow().UtcDateTime) }, tx), ct);

    public Task<int> RestoreDismissedAsync(CancellationToken ct) =>
        _writes.EnqueueAsync((conn, tx) => conn.Execute("DELETE FROM DuplicateDismissals", transaction: tx), ct);
}
