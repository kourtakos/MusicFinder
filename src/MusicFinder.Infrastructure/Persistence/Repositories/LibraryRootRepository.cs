using Dapper;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Entities;
using MusicFinder.Domain.Enums;

namespace MusicFinder.Infrastructure.Persistence.Repositories;

public sealed class LibraryRootRepository : ILibraryRootRepository
{
    private const string Columns = "Id, Path, Enabled, IncludeSubfolders, LastScanUtc, Status";
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _writes;

    public LibraryRootRepository(SqliteConnectionFactory factory, SqliteWriteQueue writes)
    {
        _factory = factory;
        _writes = writes;
    }

    public Task<LibraryRoot> AddAsync(string path, CancellationToken ct = default) =>
        _writes.EnqueueAsync((conn, tx) =>
        {
            conn.Execute("INSERT OR IGNORE INTO LibraryRoots(Path) VALUES (@path)", new { path }, tx);
            return conn.QuerySingle<LibraryRoot>($"SELECT {Columns} FROM LibraryRoots WHERE Path = @path", new { path }, tx);
        }, ct);

    public async Task<IReadOnlyList<LibraryRoot>> GetAllAsync(CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        return (await conn.QueryAsync<LibraryRoot>($"SELECT {Columns} FROM LibraryRoots ORDER BY Path")).AsList();
    }

    public async Task<LibraryRoot?> GetAsync(long id, CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        return await conn.QuerySingleOrDefaultAsync<LibraryRoot>($"SELECT {Columns} FROM LibraryRoots WHERE Id = @id", new { id });
    }

    public Task UpdateStatusAsync(long id, ScanStatus status, DateTime? lastScanUtc = null, CancellationToken ct = default) =>
        _writes.EnqueueAsync((conn, tx) =>
            conn.Execute(
                "UPDATE LibraryRoots SET Status = @status, LastScanUtc = COALESCE(@lastScanUtc, LastScanUtc) WHERE Id = @id",
                new { id, status = status.ToString(), lastScanUtc = lastScanUtc is { } d ? DapperConfig.ToDb(d) : null }, tx), ct);

    public Task SetEnabledAsync(long id, bool enabled, CancellationToken ct = default) =>
        _writes.EnqueueAsync((conn, tx) =>
            conn.Execute("UPDATE LibraryRoots SET Enabled = @enabled WHERE Id = @id", new { id, enabled }, tx), ct);

    public Task SetUnsupportedAsync(long rootId, IReadOnlyDictionary<string, int> countsByExtension, CancellationToken ct = default) =>
        _writes.EnqueueAsync((conn, tx) =>
        {
            conn.Execute("DELETE FROM ScanUnsupported WHERE RootId = @rootId", new { rootId }, tx);
            conn.Execute("INSERT INTO ScanUnsupported(RootId, Extension, FileCount) VALUES (@rootId, @ext, @n)",
                countsByExtension.Select(kv => new { rootId, ext = kv.Key, n = kv.Value }), tx);
        }, ct);

    public async Task<IReadOnlyList<(string Extension, int Count)>> GetUnsupportedAsync(CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        var rows = await conn.QueryAsync<(string Extension, long Count)>(
            "SELECT u.Extension, SUM(u.FileCount) AS Count FROM ScanUnsupported u JOIN LibraryRoots r ON r.Id = u.RootId " +
            "WHERE r.Enabled = 1 GROUP BY u.Extension ORDER BY Count DESC, u.Extension");
        return rows.Select(r => (r.Extension, (int)r.Count)).ToList();
    }
}
