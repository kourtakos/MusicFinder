using Dapper;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Entities;

namespace MusicFinder.Infrastructure.Persistence.Repositories;

public sealed class GenreRepository : IGenreRepository
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _writes;

    public GenreRepository(SqliteConnectionFactory factory, SqliteWriteQueue writes)
    {
        _factory = factory;
        _writes = writes;
    }

    public async Task<int> GetTaxonomyVersionAsync(CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        return await conn.ExecuteScalarAsync<int>("SELECT CAST(Value AS INTEGER) FROM AppState WHERE Key = 'TaxonomyVersion'");
    }

    public Task<int> BumpTaxonomyVersionAsync(CancellationToken ct = default) =>
        _writes.EnqueueAsync((conn, tx) =>
        {
            conn.Execute("UPDATE AppState SET Value = CAST(Value AS INTEGER) + 1 WHERE Key = 'TaxonomyVersion'", transaction: tx);
            return conn.ExecuteScalar<int>("SELECT CAST(Value AS INTEGER) FROM AppState WHERE Key = 'TaxonomyVersion'", transaction: tx);
        }, ct);

    public async Task<IReadOnlyList<Genre>> GetAllAsync(CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        return (await conn.QueryAsync<Genre>(
            "SELECT Id, ParentId, Name, NormalizedName, SortOrder, IsBuiltIn FROM Genres ORDER BY SortOrder, Name")).AsList();
    }

    public async Task<IReadOnlyList<GenreAlias>> GetAliasesAsync(CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        return (await conn.QueryAsync<GenreAlias>("SELECT AliasNormalized, GenreId FROM GenreAliases ORDER BY AliasNormalized")).AsList();
    }

    public Task<long> AddAsync(string name, string normalizedName, long? parentId, CancellationToken ct = default) =>
        _writes.EnqueueAsync((conn, tx) => conn.QuerySingle<long>(
            """
            INSERT INTO Genres(ParentId, Name, NormalizedName, SortOrder, IsBuiltIn)
            VALUES (@parentId, @name, @normalizedName, (SELECT COALESCE(MAX(SortOrder), 0) + 1 FROM Genres), 0)
            RETURNING Id
            """, new { parentId, name, normalizedName }, tx), ct);

    public Task UpdateNameAsync(long id, string name, string normalizedName, CancellationToken ct = default) =>
        _writes.EnqueueAsync((conn, tx) =>
            conn.Execute("UPDATE Genres SET Name = @name, NormalizedName = @normalizedName WHERE Id = @id",
                new { id, name, normalizedName }, tx), ct);

    public Task SetParentAsync(long id, long? parentId, CancellationToken ct = default) =>
        _writes.EnqueueAsync((conn, tx) =>
            conn.Execute("UPDATE Genres SET ParentId = @parentId WHERE Id = @id", new { id, parentId }, tx), ct);

    public Task DeleteAsync(long id, CancellationToken ct = default) =>
        _writes.EnqueueAsync((conn, tx) =>
        {
            conn.Execute("DELETE FROM GenreAliases WHERE GenreId = @id", new { id }, tx);
            conn.Execute("DELETE FROM TrackGenres WHERE GenreId = @id", new { id }, tx);
            conn.Execute("DELETE FROM Genres WHERE Id = @id", new { id }, tx);
        }, ct);

    public Task<bool> AddAliasAsync(string aliasNormalized, long genreId, CancellationToken ct = default) =>
        _writes.EnqueueAsync((conn, tx) =>
            conn.Execute("INSERT OR IGNORE INTO GenreAliases(AliasNormalized, GenreId) VALUES (@aliasNormalized, @genreId)",
                new { aliasNormalized, genreId }, tx) > 0, ct);

    public Task RemoveAliasAsync(string aliasNormalized, CancellationToken ct = default) =>
        _writes.EnqueueAsync((conn, tx) =>
            conn.Execute("DELETE FROM GenreAliases WHERE AliasNormalized = @aliasNormalized", new { aliasNormalized }, tx), ct);

    public async Task<IReadOnlyDictionary<long, int>> GetTrackCountsAsync(CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        // Anc maps every genre to itself and all of its descendants; grouping by the ancestor gives subtree counts.
        var rows = await conn.QueryAsync<(long GenreId, int Count)>(
            """
            WITH RECURSIVE Anc(Root, Id) AS (
                SELECT Id, Id FROM Genres
                UNION ALL
                SELECT a.Root, g.Id FROM Anc a JOIN Genres g ON g.ParentId = a.Id)
            SELECT a.Root, COUNT(DISTINCT tg.TrackId)
            FROM Anc a
            JOIN TrackGenres tg ON tg.GenreId = a.Id
            JOIN Tracks t ON t.Id = tg.TrackId AND t.Missing = 0
            GROUP BY a.Root
            """);
        return rows.ToDictionary(r => r.GenreId, r => r.Count);
    }
}

public sealed class CustomTagRepository : ICustomTagRepository
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _writes;

    public CustomTagRepository(SqliteConnectionFactory factory, SqliteWriteQueue writes)
    {
        _factory = factory;
        _writes = writes;
    }

    public async Task<IReadOnlyList<CustomTag>> GetAllAsync(CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        return (await conn.QueryAsync<CustomTag>("SELECT Id, Name, SortOrder FROM CustomTags ORDER BY SortOrder, Name")).AsList();
    }

    public Task<long> AddAsync(string name, CancellationToken ct = default) =>
        _writes.EnqueueAsync((conn, tx) => conn.QuerySingle<long>(
            "INSERT INTO CustomTags(Name, SortOrder) VALUES (@name, (SELECT COALESCE(MAX(SortOrder), 0) + 1 FROM CustomTags)) RETURNING Id",
            new { name }, tx), ct);

    public Task RenameAsync(long id, string name, CancellationToken ct = default) =>
        _writes.EnqueueAsync((conn, tx) => conn.Execute("UPDATE CustomTags SET Name = @name WHERE Id = @id", new { id, name }, tx), ct);

    public Task DeleteAsync(long id, CancellationToken ct = default) =>
        _writes.EnqueueAsync((conn, tx) =>
        {
            conn.Execute("DELETE FROM TrackCustomTags WHERE TagId = @id", new { id }, tx);
            conn.Execute("DELETE FROM CustomTags WHERE Id = @id", new { id }, tx);
        }, ct);

    public async Task<IReadOnlyDictionary<long, int>> GetTrackCountsAsync(CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        var rows = await conn.QueryAsync<(long TagId, int Count)>(
            """
            SELECT tc.TagId, COUNT(*) FROM TrackCustomTags tc
            JOIN Tracks t ON t.Id = tc.TrackId WHERE t.Missing = 0 GROUP BY tc.TagId
            """);
        return rows.ToDictionary(r => r.TagId, r => r.Count);
    }

    public async Task<IReadOnlyList<(long TrackId, long TagId)>> GetAssignmentsAsync(IReadOnlyList<long> trackIds, CancellationToken ct = default)
    {
        if (trackIds.Count == 0) return [];
        using var conn = _factory.Open();
        var rows = await conn.QueryAsync<(long TrackId, long TagId)>(
            "SELECT TrackId, TagId FROM TrackCustomTags WHERE TrackId IN @trackIds", new { trackIds });
        return rows.ToList();
    }

    public Task AssignAsync(IReadOnlyList<long> trackIds, long tagId, CancellationToken ct = default) =>
        trackIds.Count == 0
            ? Task.CompletedTask
            : _writes.EnqueueAsync((conn, tx) => conn.Execute(
                "INSERT OR IGNORE INTO TrackCustomTags(TrackId, TagId) VALUES (@trackId, @tagId)",
                trackIds.Select(trackId => new { trackId, tagId }), tx), ct);

    public Task UnassignAsync(IReadOnlyList<long> trackIds, long tagId, CancellationToken ct = default) =>
        trackIds.Count == 0
            ? Task.CompletedTask
            : _writes.EnqueueAsync((conn, tx) => conn.Execute(
                "DELETE FROM TrackCustomTags WHERE TrackId = @trackId AND TagId = @tagId",
                trackIds.Select(trackId => new { trackId, tagId }), tx), ct);
}
