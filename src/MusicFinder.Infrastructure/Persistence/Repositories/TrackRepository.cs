using Dapper;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Entities;

namespace MusicFinder.Infrastructure.Persistence.Repositories;

public sealed class TrackRepository : ITrackRepository
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _writes;

    public TrackRepository(SqliteConnectionFactory factory, SqliteWriteQueue writes)
    {
        _factory = factory;
        _writes = writes;
    }

    private const string UpsertSql =
        """
        INSERT INTO Tracks (FolderId, FullPath, FileName, Extension, FileSize, ModifiedUtc, IndexedUtc,
            Title, Artist, AlbumArtist, Album, GenreRaw, Year, TrackNumber, DurationMs, BitrateKbps, SampleRateHz,
            Bpm, MusicalKey, MusicBrainzRecordingId, ArtworkCacheKey, Missing, NormalizedText, GenreMapVersion, MetaVersion)
        VALUES (@FolderId, @FullPath, @FileName, @Extension, @FileSize, @ModifiedUtc, @IndexedUtc,
            @Title, @Artist, @AlbumArtist, @Album, @GenreRaw, @Year, @TrackNumber, @DurationMs, @BitrateKbps, @SampleRateHz,
            @Bpm, @MusicalKey, @MusicBrainzRecordingId, @ArtworkCacheKey, @Missing, @NormalizedText, 0, @MetaVersion)
        ON CONFLICT(FullPath) DO UPDATE SET
            FolderId = excluded.FolderId, FileName = excluded.FileName, Extension = excluded.Extension,
            FileSize = excluded.FileSize, ModifiedUtc = excluded.ModifiedUtc, IndexedUtc = excluded.IndexedUtc,
            Title = excluded.Title, Artist = excluded.Artist, AlbumArtist = excluded.AlbumArtist, Album = excluded.Album,
            GenreRaw = excluded.GenreRaw, Year = excluded.Year, TrackNumber = excluded.TrackNumber,
            DurationMs = excluded.DurationMs, BitrateKbps = excluded.BitrateKbps, SampleRateHz = excluded.SampleRateHz,
            Bpm = excluded.Bpm, MusicalKey = excluded.MusicalKey, MusicBrainzRecordingId = excluded.MusicBrainzRecordingId,
            ArtworkCacheKey = excluded.ArtworkCacheKey, Missing = excluded.Missing, NormalizedText = excluded.NormalizedText,
            GenreMapVersion = 0, SearchAliasVersion = 0, MetaVersion = excluded.MetaVersion
        """;

    public Task UpsertBatchAsync(IReadOnlyList<Track> tracks, CancellationToken ct = default) =>
        tracks.Count == 0
            ? Task.CompletedTask
            : _writes.EnqueueAsync((conn, tx) => conn.Execute(UpsertSql, tracks, tx), ct);

    public async Task<Track?> GetAsync(long id, CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        return await conn.QuerySingleOrDefaultAsync<Track>("SELECT * FROM Tracks WHERE Id = @id", new { id });
    }

    public async Task<IReadOnlyList<Track>> GetManyAsync(IReadOnlyList<long> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return [];
        using var conn = _factory.Open();
        return (await conn.QueryAsync<Track>("SELECT * FROM Tracks WHERE Id IN @ids", new { ids })).AsList();
    }

    public Task ApplyMetadataAsync(long trackId, Domain.Models.TrackMetadata m, long fileSize, DateTime modifiedUtc, string normalizedText,
        CancellationToken ct = default) =>
        _writes.EnqueueAsync((conn, tx) => conn.Execute(
            """
            UPDATE Tracks SET
                Title = @Title, Artist = @Artist, AlbumArtist = @AlbumArtist, Album = @Album, GenreRaw = @GenreRaw,
                Year = @Year, TrackNumber = @TrackNumber, DurationMs = @DurationMs, BitrateKbps = @BitrateKbps,
                SampleRateHz = @SampleRateHz, Bpm = @Bpm, MusicalKey = @MusicalKey,
                MusicBrainzRecordingId = @MusicBrainzRecordingId, ArtworkCacheKey = @ArtworkHash,
                FileSize = @fileSize, ModifiedUtc = @modifiedUtc, NormalizedText = @normalizedText,
                GenreMapVersion = 0, SearchAliasVersion = 0, MetaVersion = @metaVersion, Missing = 0
            WHERE Id = @trackId
            """,
            new
            {
                trackId, m.Title, m.Artist, m.AlbumArtist, m.Album, m.GenreRaw, m.Year, m.TrackNumber, m.DurationMs,
                m.BitrateKbps, m.SampleRateHz, m.Bpm, m.MusicalKey, m.MusicBrainzRecordingId, m.ArtworkHash,
                fileSize, modifiedUtc, normalizedText, metaVersion = Track.CurrentMetaVersion
            }, tx), ct);

    public async Task<IReadOnlyList<IndexedFile>> GetIndexedFilesAsync(long rootId, CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        var rows = await conn.QueryAsync<(long Id, string FullPath, long FileSize, DateTime ModifiedUtc, int MetaVersion)>(
            """
            SELECT t.Id, t.FullPath, t.FileSize, t.ModifiedUtc, t.MetaVersion FROM Tracks t
            JOIN Folders f ON f.Id = t.FolderId WHERE f.RootId = @rootId
            """, new { rootId });
        return rows.Select(r => new IndexedFile(r.Id, r.FullPath, r.FileSize, r.ModifiedUtc, r.MetaVersion)).ToList();
    }

    public Task TouchAsync(IReadOnlyList<long> ids, DateTime scanUtc, CancellationToken ct = default) =>
        ids.Count == 0
            ? Task.CompletedTask
            : _writes.EnqueueAsync((conn, tx) => conn.Execute(
                "UPDATE Tracks SET IndexedUtc = @scanUtc, Missing = 0 WHERE Id = @id",
                ids.Select(id => new { id, scanUtc }), tx), ct);

    public Task<int> MarkMissingAsync(long rootId, DateTime scanStartUtc, CancellationToken ct = default) =>
        _writes.EnqueueAsync((conn, tx) => conn.Execute(
            """
            UPDATE Tracks SET Missing = 1
            WHERE Missing = 0 AND IndexedUtc < @scanStartUtc
              AND FolderId IN (SELECT Id FROM Folders WHERE RootId = @rootId)
            """, new { rootId, scanStartUtc }, tx), ct);

    public async Task<IReadOnlyList<string>> GetDistinctGenresAsync(CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        return (await conn.QueryAsync<string>(
            "SELECT DISTINCT GenreRaw FROM Tracks WHERE Missing = 0 AND GenreRaw IS NOT NULL AND GenreRaw <> ''")).AsList();
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        using var conn = _factory.Open();
        return await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Tracks WHERE Missing = 0");
    }
}
