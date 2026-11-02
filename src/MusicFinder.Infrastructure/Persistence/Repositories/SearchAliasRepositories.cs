using Dapper;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.Infrastructure.Persistence.Repositories;

public sealed class TrackSearchAliasRepository : ITrackSearchAliasRepository
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _writes;

    public TrackSearchAliasRepository(SqliteConnectionFactory factory, SqliteWriteQueue writes)
    {
        _factory = factory;
        _writes = writes;
    }

    public async Task<IReadOnlyList<SearchAliasStaleRow>> GetStaleAsync(int version, int limit, CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        var rows = await conn.QueryAsync<SearchAliasStaleRow>(
            @"SELECT Id, FullPath, FileName, Title, Artist, AlbumArtist, Album, GenreRaw
              FROM Tracks WHERE SearchAliasVersion < @version ORDER BY Id LIMIT @limit", new { version, limit });
        return rows.ToList();
    }

    public async Task<int> CountStaleAsync(int version, CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        return await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Tracks WHERE SearchAliasVersion < @version", new { version });
    }

    public Task<int> UpsertAsync(IReadOnlyList<(SearchAliasStaleRow Source, TrackSearchAlias Alias)> rows, int version, CancellationToken ct = default) =>
        rows.Count == 0
            ? Task.FromResult(0)
            : _writes.EnqueueAsync((conn, tx) =>
            {
                var applied = 0;
                foreach (var (s, a) in rows)
                {
                    // Stamp only if the text is still what the row was built from (`IS` compares NULLs safely).
                    var stamped = conn.Execute(
                        @"UPDATE Tracks SET SearchAliasVersion = @version
                          WHERE Id = @Id AND Title IS @Title AND Artist IS @Artist AND AlbumArtist IS @AlbumArtist
                            AND Album IS @Album AND GenreRaw IS @GenreRaw AND FileName = @FileName AND FullPath = @FullPath",
                        new { version, s.Id, s.Title, s.Artist, s.AlbumArtist, s.Album, s.GenreRaw, s.FileName, s.FullPath }, tx);
                    if (stamped == 0) continue;

                    conn.Execute(
                        @"INSERT INTO TrackSearchAliases(TrackId, TitleN, ArtistN, AlbumN, GenreN, FileN, TitleK, ArtistK, AlbumK, GenreK, FileK, Extra)
                          VALUES (@TrackId, @TitleN, @ArtistN, @AlbumN, @GenreN, @FileN, @TitleK, @ArtistK, @AlbumK, @GenreK, @FileK, @Extra)
                          ON CONFLICT(TrackId) DO UPDATE SET
                            TitleN = excluded.TitleN, ArtistN = excluded.ArtistN, AlbumN = excluded.AlbumN, GenreN = excluded.GenreN, FileN = excluded.FileN,
                            TitleK = excluded.TitleK, ArtistK = excluded.ArtistK, AlbumK = excluded.AlbumK, GenreK = excluded.GenreK, FileK = excluded.FileK,
                            Extra = excluded.Extra", a, tx);
                    applied++;
                }
                return applied;
            }, ct);

    public Task MarkStaleAsync(IReadOnlyList<long> trackIds, CancellationToken ct = default) =>
        trackIds.Count == 0
            ? Task.CompletedTask
            : _writes.EnqueueAsync((conn, tx) =>
            {
                foreach (var chunk in trackIds.Chunk(500))
                    conn.Execute("UPDATE Tracks SET SearchAliasVersion = 0 WHERE Id IN @chunk", new { chunk }, tx);
            }, ct);

    public async Task<IReadOnlyList<long>> FindContainingAsync(string normalizedPhrase, CancellationToken ct = default)
    {
        if (normalizedPhrase.Length == 0) return [];
        using var conn = _factory.Open();
        // NormalizedText holds the normalized words of the track; a quoted FTS phrase matches them as whole words in order.
        var phrase = "\"" + normalizedPhrase.Replace("\"", "\"\"") + "\"";
        var ids = await conn.QueryAsync<long>(
            "SELECT rowid FROM TrackSearch WHERE TrackSearch MATCH @match", new { match = "NormalizedText:" + phrase });
        return ids.ToList();
    }

    public async Task<IReadOnlyList<long>> FindWithExtraAsync(string normalizedAlias, CancellationToken ct = default)
    {
        if (normalizedAlias.Length == 0) return [];
        using var conn = _factory.Open();
        var phrase = "\"" + normalizedAlias.Replace("\"", "\"\"") + "\"";
        var ids = await conn.QueryAsync<long>(
            "SELECT rowid FROM TrackAliasSearch WHERE TrackAliasSearch MATCH @match", new { match = "Extra:" + phrase });
        return ids.ToList();
    }
}

public sealed class SearchAliasOverrideRepository : ISearchAliasOverrideRepository
{
    private const string Columns = "Id, Source, Alias, SourceNormalized, AliasNormalized";
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _writes;

    public SearchAliasOverrideRepository(SqliteConnectionFactory factory, SqliteWriteQueue writes)
    {
        _factory = factory;
        _writes = writes;
    }

    public async Task<IReadOnlyList<SearchAliasOverride>> GetAllAsync(CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        var rows = await conn.QueryAsync<SearchAliasOverride>($"SELECT {Columns} FROM SearchAliasOverrides ORDER BY Id");
        return rows.ToList();
    }

    public Task<SearchAliasOverride?> AddAsync(string source, string alias, string sourceNormalized, string aliasNormalized, CancellationToken ct = default) =>
        _writes.EnqueueAsync((conn, tx) =>
        {
            var created = DapperConfig.ToDb(DateTime.UtcNow);
            var inserted = conn.Execute(
                @"INSERT INTO SearchAliasOverrides(Source, Alias, SourceNormalized, AliasNormalized, CreatedUtc)
                  VALUES (@source, @alias, @sourceNormalized, @aliasNormalized, @created)
                  ON CONFLICT(SourceNormalized, AliasNormalized) DO NOTHING",
                new { source, alias, sourceNormalized, aliasNormalized, created }, tx);
            if (inserted == 0) return (SearchAliasOverride?)null;
            return conn.QuerySingle<SearchAliasOverride>(
                $"SELECT {Columns} FROM SearchAliasOverrides WHERE SourceNormalized = @sourceNormalized AND AliasNormalized = @aliasNormalized",
                new { sourceNormalized, aliasNormalized }, tx);
        }, ct);

    public Task<SearchAliasOverride?> RemoveAsync(long id, CancellationToken ct = default) =>
        _writes.EnqueueAsync((conn, tx) =>
        {
            var existing = conn.QuerySingleOrDefault<SearchAliasOverride>($"SELECT {Columns} FROM SearchAliasOverrides WHERE Id = @id", new { id }, tx);
            if (existing is not null) conn.Execute("DELETE FROM SearchAliasOverrides WHERE Id = @id", new { id }, tx);
            return existing;
        }, ct);
}
