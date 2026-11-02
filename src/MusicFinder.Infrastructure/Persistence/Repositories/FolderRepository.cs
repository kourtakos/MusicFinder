using Dapper;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Entities;

namespace MusicFinder.Infrastructure.Persistence.Repositories;

public sealed class FolderRepository : IFolderRepository
{
    private const string Columns = "Id, RootId, ParentId, FullPath, Name, DirectTrackCount, RecursiveTrackCount";
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _writes;

    public FolderRepository(SqliteConnectionFactory factory, SqliteWriteQueue writes)
    {
        _factory = factory;
        _writes = writes;
    }

    public Task<long> UpsertAsync(long rootId, long? parentId, string fullPath, string name, CancellationToken ct = default) =>
        _writes.EnqueueAsync((conn, tx) => conn.QuerySingle<long>(
            """
            INSERT INTO Folders(RootId, ParentId, FullPath, Name) VALUES (@rootId, @parentId, @fullPath, @name)
            ON CONFLICT(FullPath) DO UPDATE SET RootId = excluded.RootId, ParentId = excluded.ParentId, Name = excluded.Name
            RETURNING Id
            """,
            new { rootId, parentId, fullPath, name }, tx), ct);

    public async Task<Folder?> GetAsync(long id, CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        return await conn.QuerySingleOrDefaultAsync<Folder>($"SELECT {Columns} FROM Folders WHERE Id = @id", new { id });
    }

    public async Task<IReadOnlyList<Folder>> GetChildrenAsync(long rootId, long? parentId, CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        // Top level of a root = the root's folder row(s) with no parent.
        var sql = parentId is null
            ? $"SELECT {Columns} FROM Folders WHERE RootId = @rootId AND ParentId IS NULL ORDER BY Name COLLATE NOCASE"
            : $"SELECT {Columns} FROM Folders WHERE RootId = @rootId AND ParentId = @parentId ORDER BY Name COLLATE NOCASE";
        return (await conn.QueryAsync<Folder>(sql, new { rootId, parentId })).AsList();
    }

    public Task RecomputeCountsAsync(long rootId, CancellationToken ct = default) =>
        _writes.EnqueueAsync((conn, tx) =>
        {
            var folders = conn.Query<(long Id, long? ParentId)>(
                "SELECT Id, ParentId FROM Folders WHERE RootId = @rootId", new { rootId }, tx).AsList();
            var direct = conn.Query<(long FolderId, int Count)>(
                """
                SELECT t.FolderId, COUNT(*) FROM Tracks t
                JOIN Folders f ON f.Id = t.FolderId
                WHERE f.RootId = @rootId AND t.Missing = 0 GROUP BY t.FolderId
                """, new { rootId }, tx).ToDictionary(x => x.FolderId, x => x.Count);

            var parentOf = folders.ToDictionary(f => f.Id, f => f.ParentId);
            var recursive = folders.ToDictionary(f => f.Id, f => direct.GetValueOrDefault(f.Id));
            // Push each folder's direct count up through all its ancestors.
            foreach (var (id, count) in direct)
            {
                if (!parentOf.TryGetValue(id, out var p)) continue;
                while (p is { } pid && recursive.ContainsKey(pid))
                {
                    recursive[pid] += count;
                    p = parentOf[pid];
                }
            }

            conn.Execute(
                "UPDATE Folders SET DirectTrackCount = @direct, RecursiveTrackCount = @recursive WHERE Id = @id",
                folders.Select(f => new { id = f.Id, direct = direct.GetValueOrDefault(f.Id), recursive = recursive[f.Id] }), tx);
        }, ct);
}
