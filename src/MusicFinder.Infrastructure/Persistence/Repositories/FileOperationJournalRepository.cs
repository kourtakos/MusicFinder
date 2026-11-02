using Dapper;
using Microsoft.Data.Sqlite;
using MusicFinder.Application.Services;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.Infrastructure.Persistence.Repositories;

/// <summary>The journal of file operations, and the one place that moves a track's database row to its new path.</summary>
public sealed class FileOperationJournalRepository : IFileOperationJournal, IRenameSourceReader
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _writes;
    private readonly TimeProvider _time;
    private readonly MusicFinder.Domain.Contracts.IFolderRepository? _folders;

    public FileOperationJournalRepository(SqliteConnectionFactory factory, SqliteWriteQueue writes, MusicFinder.Domain.Contracts.IFolderRepository? folders = null, TimeProvider? time = null)
    {
        _folders = folders;
        _factory = factory;
        _writes = writes;
        _time = time ?? TimeProvider.System;
    }

    private string Now() => DapperConfig.ToDb(_time.GetUtcNow().UtcDateTime);

    public Task<(string BatchId, IReadOnlyList<JournalOp> Ops)> BeginBatchAsync(
        string kind, string? template, IReadOnlyList<NewJournalOp> ops, CancellationToken ct) =>
        _writes.EnqueueAsync((conn, tx) =>
        {
            var id = Guid.NewGuid().ToString("N");
            var now = Now();
            conn.Execute("INSERT INTO FileOperationBatches(Id, Kind, Template, CreatedUtc) VALUES (@id, @kind, @template, @now)",
                new { id, kind, template, now }, tx);
            var result = new List<JournalOp>(ops.Count);
            foreach (var o in ops)
            {
                var opId = conn.ExecuteScalar<long>(
                    """
                    INSERT INTO FileOperations(BatchId, TrackId, Role, OldPath, NewPath, State, CreatedUtc)
                    VALUES (@id, @TrackId, @Role, @OldPath, @NewPath, 'Planned', @now);
                    SELECT last_insert_rowid();
                    """, new { id, o.TrackId, o.Role, o.OldPath, o.NewPath, now }, tx);
                result.Add(new JournalOp(opId, id, o.TrackId, o.Role, o.OldPath, o.NewPath, OpState.Planned, null));
            }
            return (id, (IReadOnlyList<JournalOp>)result);
        }, ct);

    public Task SetStateAsync(long opId, string state, string? error, string? trackPath, CancellationToken ct) =>
        _writes.EnqueueAsync((conn, tx) =>
        {
            var finished = state is OpState.Done or OpState.Failed or OpState.Skipped or OpState.Undone;
            conn.Execute("UPDATE FileOperations SET State = @state, Error = @error, CompletedUtc = @completed WHERE Id = @opId",
                new { opId, state, error, completed = finished ? Now() : null }, tx);

            if (trackPath is null) return 0;
            var trackId = conn.ExecuteScalar<long>("SELECT TrackId FROM FileOperations WHERE Id = @opId", new { opId }, tx);
            var t = conn.QuerySingleOrDefault<(string? Title, string? Artist, string? AlbumArtist, string? Album, string? GenreRaw)>(
                "SELECT Title, Artist, AlbumArtist, Album, GenreRaw FROM Tracks WHERE Id = @trackId", new { trackId }, tx);
            var fileName = Path.GetFileName(trackPath);
            var folderId = EnsureFolder(conn, tx, trackId, Path.GetDirectoryName(trackPath)!);
            // Same row, new path: play history, genres, tags, analysis and identification all follow it. The triggers refresh the search indexes.
            conn.Execute(
                "UPDATE Tracks SET FullPath = @trackPath, FileName = @fileName, FolderId = @folderId, NormalizedText = @text WHERE Id = @trackId",
                new { trackId, trackPath, fileName, folderId, text = TextNormalizer.BuildSearchText(fileName, t.Title, t.Artist, t.AlbumArtist, t.Album, t.GenreRaw) }, tx);
            return 1;
        }, ct);

    /// <summary>The id of the folder row for a directory below the track's library folder, creating the missing rows (a scan would find the same ones).</summary>
    private static long EnsureFolder(SqliteConnection conn, SqliteTransaction tx, long trackId, string directory)
    {
        var root = conn.QuerySingle<(long Id, string Path)>(
            "SELECT r.Id, r.Path FROM Tracks t JOIN Folders f ON f.Id = t.FolderId JOIN LibraryRoots r ON r.Id = f.RootId WHERE t.Id = @trackId", new { trackId }, tx);
        return Ensure(directory);

        long Ensure(string dir)
        {
            var id = conn.ExecuteScalar<long?>("SELECT Id FROM Folders WHERE FullPath = @dir COLLATE NOCASE", new { dir }, tx);
            if (id is not null) return id.Value;
            var parent = Path.GetDirectoryName(dir);
            if (parent is null || !dir.StartsWith(root.Path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The folder is outside the library folder.");
            var parentId = Ensure(parent);
            conn.Execute("INSERT INTO Folders(RootId, ParentId, FullPath, Name) VALUES (@rootId, @parentId, @dir, @name)",
                new { rootId = root.Id, parentId, dir, name = Path.GetFileName(dir) }, tx);
            return conn.ExecuteScalar<long>("SELECT last_insert_rowid()", transaction: tx);
        }
    }

    public async Task RecomputeFolderCountsAsync(string batchId, CancellationToken ct)
    {
        if (_folders is null) return;
        using var conn = _factory.Open();
        var roots = await conn.QueryAsync<long>(new CommandDefinition(
            "SELECT DISTINCT f.RootId FROM FileOperations o JOIN Tracks t ON t.Id = o.TrackId JOIN Folders f ON f.Id = t.FolderId WHERE o.BatchId = @batchId",
            new { batchId }, cancellationToken: ct));
        foreach (var root in roots) await _folders.RecomputeCountsAsync(root, ct);
    }

    private sealed record OpRow(long Id, string BatchId, long TrackId, string Role, string OldPath, string NewPath, string State, string? Error);

    private static JournalOp ToOp(OpRow r) => new(r.Id, r.BatchId, r.TrackId, r.Role, r.OldPath, r.NewPath, r.State, r.Error);

    public async Task<IReadOnlyList<JournalOp>> GetOpsAsync(string batchId, CancellationToken ct)
    {
        using var conn = _factory.Open();
        var rows = await conn.QueryAsync<OpRow>(new CommandDefinition(
            "SELECT Id, BatchId, TrackId, Role, OldPath, NewPath, State, Error FROM FileOperations WHERE BatchId = @batchId ORDER BY Id",
            new { batchId }, cancellationToken: ct));
        return rows.Select(ToOp).ToList();
    }

    public async Task<IReadOnlyList<JournalOp>> GetUnfinishedAsync(CancellationToken ct)
    {
        using var conn = _factory.Open();
        var rows = await conn.QueryAsync<OpRow>(new CommandDefinition(
            "SELECT Id, BatchId, TrackId, Role, OldPath, NewPath, State, Error FROM FileOperations WHERE State IN ('InProgress','UndoInProgress') ORDER BY Id",
            cancellationToken: ct));
        return rows.Select(ToOp).ToList();
    }

    // A class with settable properties, not a positional record: with no rows Dapper cannot match a constructor to expression columns.
    private sealed class BatchRow
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "";
        public string? Template { get; set; }
        public string CreatedUtc { get; set; } = "";
        public long Done { get; set; }
        public long Failed { get; set; }
        public long Undone { get; set; }
        public long Skipped { get; set; }
    }

    public async Task<IReadOnlyList<BatchInfo>> GetBatchesAsync(int take, CancellationToken ct)
    {
        using var conn = _factory.Open();
        var rows = await conn.QueryAsync<BatchRow>(new CommandDefinition(
            """
            SELECT b.Id, b.Kind, b.Template, b.CreatedUtc,
                   COALESCE(SUM(o.State = 'Done'), 0) AS Done, COALESCE(SUM(o.State = 'Failed'), 0) AS Failed,
                   COALESCE(SUM(o.State = 'Undone'), 0) AS Undone, COALESCE(SUM(o.State = 'Skipped'), 0) AS Skipped
            FROM FileOperationBatches b LEFT JOIN FileOperations o ON o.BatchId = b.Id AND o.Role = 'Track'
            GROUP BY b.Id ORDER BY b.CreatedUtc DESC, b.rowid DESC LIMIT @take
            """, new { take }, cancellationToken: ct));
        return rows.Select(r => new BatchInfo(r.Id, r.Kind, r.Template, DapperConfig.FromDb(r.CreatedUtc), (int)r.Done, (int)r.Failed, (int)r.Undone, (int)r.Skipped)).ToList();
    }

    public async Task<string?> GetTrackPathAsync(long trackId, CancellationToken ct)
    {
        using var conn = _factory.Open();
        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition("SELECT FullPath FROM Tracks WHERE Id = @trackId", new { trackId }, cancellationToken: ct));
    }

    private sealed record SourceRow(long Id, string FullPath, string? Artist, string? Title, string? Album, string? AlbumArtist,
        long? Year, long? TrackNumber, string? GenreRaw, double? Bpm, string? MusicalKey, string? RootPath);

    public async Task<IReadOnlyList<RenameSource>> GetAsync(IReadOnlyCollection<long> trackIds, CancellationToken ct)
    {
        if (trackIds.Count == 0) return [];
        using var conn = _factory.Open();
        var ids = trackIds.ToArray();
        var rows = (await conn.QueryAsync<SourceRow>(new CommandDefinition(
            """
            SELECT t.Id, t.FullPath, t.Artist, t.Title, t.Album, t.AlbumArtist, t.Year, t.TrackNumber, t.GenreRaw, t.Bpm, t.MusicalKey, r.Path AS RootPath
            FROM Tracks t JOIN Folders f ON f.Id = t.FolderId JOIN LibraryRoots r ON r.Id = f.RootId
            WHERE t.Id IN @ids AND t.Missing = 0
            """, new { ids }, cancellationToken: ct))).ToList();

        // {Genre} is the taxonomy's top genre; a track without a mapped genre falls back to its raw tag.
        var tops = await conn.QueryAsync<(long TrackId, string Name)>(new CommandDefinition(
            """
            WITH RECURSIVE up(TrackId, ParentId, Name, Depth) AS (
              SELECT tg.TrackId, g.ParentId, g.Name, 0 FROM TrackGenres tg JOIN Genres g ON g.Id = tg.GenreId WHERE tg.TrackId IN @ids
              UNION ALL
              SELECT up.TrackId, g.ParentId, g.Name, up.Depth + 1 FROM up JOIN Genres g ON g.Id = up.ParentId
            )
            SELECT TrackId, Name FROM up WHERE ParentId IS NULL ORDER BY TrackId, Name
            """, new { ids }, cancellationToken: ct));
        var top = new Dictionary<long, string>();
        foreach (var (trackId, name) in tops) top.TryAdd(trackId, name);

        return rows.Select(r => new RenameSource(
            r.Id, r.FullPath, r.Artist, r.Title, r.Album, r.AlbumArtist, (int?)r.Year, (int?)r.TrackNumber,
            top.TryGetValue(r.Id, out var g) ? g : r.GenreRaw, r.Bpm, r.MusicalKey, r.RootPath)).ToList();
    }
}
