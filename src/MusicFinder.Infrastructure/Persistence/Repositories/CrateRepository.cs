using Dapper;
using Microsoft.Data.Sqlite;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Entities;

namespace MusicFinder.Infrastructure.Persistence.Repositories;

public sealed class CrateRepository : ICrateRepository
{
    private const int SqliteConstraint = 19;
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _writes;

    public CrateRepository(SqliteConnectionFactory factory, SqliteWriteQueue writes)
    {
        _factory = factory;
        _writes = writes;
    }

    private static string Now() => DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    public async Task<IReadOnlyList<Crate>> GetAllAsync(CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        return (await conn.QueryAsync<Crate>(
            """
            SELECT c.Id, c.Name,
                   (SELECT COUNT(*) FROM CrateTracks ct JOIN Tracks t ON t.Id = ct.TrackId
                    WHERE ct.CrateId = c.Id AND t.Missing = 0) AS TrackCount
            FROM Crates c ORDER BY c.Name COLLATE NOCASE
            """)).AsList();
    }

    public Task<long?> CreateAsync(string name, CancellationToken ct = default) =>
        _writes.EnqueueAsync<long?>((conn, tx) =>
        {
            try
            {
                return conn.QuerySingle<long>("INSERT INTO Crates(Name, CreatedUtc) VALUES (@name, @now) RETURNING Id",
                    new { name, now = Now() }, tx);
            }
            catch (SqliteException e) when (e.SqliteErrorCode == SqliteConstraint) { return null; }
        }, ct);

    public Task<bool> RenameAsync(long id, string name, CancellationToken ct = default) =>
        _writes.EnqueueAsync((conn, tx) =>
        {
            try { return conn.Execute("UPDATE Crates SET Name = @name WHERE Id = @id", new { id, name }, tx) > 0; }
            catch (SqliteException e) when (e.SqliteErrorCode == SqliteConstraint) { return false; }
        }, ct);

    public Task DeleteAsync(long id, CancellationToken ct = default) =>
        _writes.EnqueueAsync((conn, tx) =>
        {
            conn.Execute("DELETE FROM CrateTracks WHERE CrateId = @id", new { id }, tx);
            conn.Execute("DELETE FROM Crates WHERE Id = @id", new { id }, tx);
        }, ct);

    public async Task<IReadOnlyList<long>> GetTrackIdsAsync(long crateId, CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        return (await conn.QueryAsync<long>(
            "SELECT TrackId FROM CrateTracks WHERE CrateId = @crateId ORDER BY Position, TrackId", new { crateId })).AsList();
    }

    public Task<int> AddTracksAsync(long crateId, IReadOnlyList<long> trackIds, CancellationToken ct = default) =>
        trackIds.Count == 0
            ? Task.FromResult(0)
            : _writes.EnqueueAsync((conn, tx) =>
            {
                var position = conn.ExecuteScalar<long>(
                    "SELECT COALESCE(MAX(Position), 0) FROM CrateTracks WHERE CrateId = @crateId", new { crateId }, tx);
                var now = Now();
                var added = 0;
                foreach (var trackId in trackIds.Distinct())
                {
                    var inserted = conn.Execute(
                        """
                        INSERT OR IGNORE INTO CrateTracks(CrateId, TrackId, Position, AddedUtc)
                        SELECT @crateId, @trackId, @position, @now WHERE EXISTS (SELECT 1 FROM Tracks WHERE Id = @trackId)
                        """, new { crateId, trackId, position = position + 1, now }, tx);
                    if (inserted > 0) { position++; added++; }
                }
                return added;
            }, ct);

    public Task RemoveTracksAsync(long crateId, IReadOnlyList<long> trackIds, CancellationToken ct = default) =>
        trackIds.Count == 0
            ? Task.CompletedTask
            : _writes.EnqueueAsync((conn, tx) => conn.Execute(
                "DELETE FROM CrateTracks WHERE CrateId = @crateId AND TrackId = @trackId",
                trackIds.Select(trackId => new { crateId, trackId }), tx), ct);

    public Task ReorderAsync(long crateId, IReadOnlyList<long> orderedTrackIds, CancellationToken ct = default) =>
        _writes.EnqueueAsync((conn, tx) =>
        {
            var current = conn.Query<long>("SELECT TrackId FROM CrateTracks WHERE CrateId = @crateId ORDER BY Position, TrackId",
                new { crateId }, tx).ToList();
            var inCrate = current.ToHashSet();
            var order = orderedTrackIds.Where(inCrate.Contains).Distinct().ToList();
            var listed = order.ToHashSet();
            order.AddRange(current.Where(t => !listed.Contains(t)));
            var position = 0;
            foreach (var trackId in order)
                conn.Execute("UPDATE CrateTracks SET Position = @position WHERE CrateId = @crateId AND TrackId = @trackId",
                    new { crateId, trackId, position = ++position }, tx);
        }, ct);
}
