using Dapper;
using Microsoft.Data.Sqlite;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Entities;

namespace MusicFinder.Infrastructure.Persistence.Repositories;

public sealed class SavedSearchRepository : ISavedSearchRepository
{
    private const int SqliteConstraint = 19;
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _writes;

    public SavedSearchRepository(SqliteConnectionFactory factory, SqliteWriteQueue writes)
    {
        _factory = factory;
        _writes = writes;
    }

    public async Task<IReadOnlyList<SavedSearch>> GetAllAsync(CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        return (await conn.QueryAsync<SavedSearch>(
            "SELECT Id, Name, StateJson FROM SavedSearches ORDER BY Name COLLATE NOCASE")).AsList();
    }

    public Task<long?> AddAsync(string name, string stateJson, CancellationToken ct = default) =>
        _writes.EnqueueAsync<long?>((conn, tx) =>
        {
            try
            {
                return conn.QuerySingle<long>(
                    "INSERT INTO SavedSearches(Name, StateJson, CreatedUtc) VALUES (@name, @stateJson, @now) RETURNING Id",
                    new { name, stateJson, now = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture) }, tx);
            }
            catch (SqliteException e) when (e.SqliteErrorCode == SqliteConstraint) { return null; }
        }, ct);

    public Task DeleteAsync(long id, CancellationToken ct = default) =>
        _writes.EnqueueAsync((conn, tx) => conn.Execute("DELETE FROM SavedSearches WHERE Id = @id", new { id }, tx), ct);
}
