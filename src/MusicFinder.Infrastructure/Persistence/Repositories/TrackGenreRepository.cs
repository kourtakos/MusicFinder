using Dapper;
using MusicFinder.Domain.Contracts;

namespace MusicFinder.Infrastructure.Persistence.Repositories;

public sealed class TrackGenreRepository : ITrackGenreRepository
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _writes;

    public TrackGenreRepository(SqliteConnectionFactory factory, SqliteWriteQueue writes)
    {
        _factory = factory;
        _writes = writes;
    }

    public async Task<IReadOnlyList<(long TrackId, string? RawGenre)>> GetStaleAsync(int version, int limit, CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        var rows = await conn.QueryAsync<(long Id, string? GenreRaw)>(
            "SELECT Id, GenreRaw FROM Tracks WHERE GenreMapVersion < @version ORDER BY Id LIMIT @limit", new { version, limit });
        return rows.Select(r => (r.Id, r.GenreRaw)).ToList();
    }

    public Task SetUserGenreAsync(IReadOnlyList<long> trackIds, long genreId, bool assigned, CancellationToken ct = default) =>
        trackIds.Count == 0
            ? Task.CompletedTask
            : _writes.EnqueueAsync((conn, tx) => conn.Execute(
                assigned
                    ? "INSERT OR IGNORE INTO TrackGenres(TrackId, GenreId, Source) VALUES (@trackId, @genreId, 'User')"
                    : "DELETE FROM TrackGenres WHERE TrackId = @trackId AND GenreId = @genreId AND Source = 'User'",
                trackIds.Select(trackId => new { trackId, genreId }), tx), ct);

    public async Task<IReadOnlyList<(long TrackId, long GenreId, Domain.Enums.GenreSource Source)>> GetForTracksAsync(
        IReadOnlyList<long> trackIds, CancellationToken ct = default)
    {
        if (trackIds.Count == 0) return [];
        using var conn = _factory.Open();
        var rows = await conn.QueryAsync<(long TrackId, long GenreId, string Source)>(
            "SELECT TrackId, GenreId, Source FROM TrackGenres WHERE TrackId IN @trackIds", new { trackIds });
        return rows.Select(r => (r.TrackId, r.GenreId, Enum.Parse<Domain.Enums.GenreSource>(r.Source))).ToList();
    }

    public async Task<IReadOnlyList<(string RawGenre, int Count)>> GetRawGenreCountsAsync(CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        var rows = await conn.QueryAsync<(string Raw, int Count)>(
            "SELECT GenreRaw, COUNT(*) FROM Tracks WHERE Missing = 0 AND GenreRaw IS NOT NULL AND TRIM(GenreRaw) <> '' GROUP BY GenreRaw");
        return rows.Select(r => (r.Raw, r.Count)).ToList();
    }

    public Task<int> ReplaceEmbeddedAsync(IReadOnlyList<TrackGenreMapping> mappings, int version, CancellationToken ct = default) =>
        mappings.Count == 0
            ? Task.FromResult(0)
            : _writes.EnqueueAsync((conn, tx) =>
            {
                var applied = 0;
                foreach (var m in mappings)
                {
                    // Only stamp the row if its raw genre is still what we mapped (`IS` is NULL-safe).
                    var stamped = conn.Execute(
                        "UPDATE Tracks SET GenreMapVersion = @version WHERE Id = @TrackId AND GenreRaw IS @RawGenre",
                        new { version, m.TrackId, m.RawGenre }, tx);
                    if (stamped == 0) continue;

                    conn.Execute("DELETE FROM TrackGenres WHERE TrackId = @TrackId AND Source = 'Embedded'", new { m.TrackId }, tx);
                    foreach (var genreId in m.GenreIds)
                        conn.Execute("INSERT OR IGNORE INTO TrackGenres(TrackId, GenreId, Source) VALUES (@TrackId, @genreId, 'Embedded')",
                            new { m.TrackId, genreId }, tx);
                    applied++;
                }
                return applied;
            }, ct);
}
