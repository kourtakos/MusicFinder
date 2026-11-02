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
}
