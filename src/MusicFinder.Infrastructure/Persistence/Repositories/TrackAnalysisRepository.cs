using System.Text.Json;
using Dapper;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.Infrastructure.Persistence.Repositories;

/// <summary>Remembered BPM and key estimates, per file identity (size + modified time) and analyzer version.</summary>
public sealed class TrackAnalysisRepository : ITrackAnalysisStore
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _writes;

    public TrackAnalysisRepository(SqliteConnectionFactory factory, SqliteWriteQueue writes)
    {
        _factory = factory;
        _writes = writes;
    }

    private sealed record Row(
        long FileSize, string ModifiedUtc, long AnalyzerVersion, string BpmStatus, double? BpmEstimate, double? BpmScore, string? BpmLevel,
        string? BpmFlags, string? BpmAlternativesJson, string KeyStatus, string? KeyEstimate, double? KeyScore, string? KeyLevel, string? KeyRunner,
        string? KeyFlags, double? TuningCents, double AnalyzedSeconds, string Decoder, string? Message, string AnalyzedUtc);

    public async Task<TrackAnalysis?> GetAsync(long trackId, long fileSize, DateTime modifiedUtc, int analyzerVersion, CancellationToken ct)
    {
        using var conn = _factory.Open();
        var row = await conn.QuerySingleOrDefaultAsync<Row>(@"SELECT FileSize, ModifiedUtc, AnalyzerVersion, BpmStatus, BpmEstimate, BpmScore, BpmLevel, BpmFlags, BpmAlternativesJson, KeyStatus, KeyEstimate,
                     KeyScore, KeyLevel, KeyRunner, KeyFlags, TuningCents, AnalyzedSeconds, Decoder, Message, AnalyzedUtc
              FROM TrackAnalysis WHERE TrackId = @trackId", new { trackId });
        if (row is null || row.FileSize != fileSize || row.AnalyzerVersion != analyzerVersion
            || DapperConfig.FromDb(row.ModifiedUtc) != UtcTime.Normalize(modifiedUtc)) return null;

        BpmEstimate? bpm = null;
        if (row.BpmStatus == "Estimated" && row.BpmEstimate is { } b)
        {
            var alternatives = string.IsNullOrEmpty(row.BpmAlternativesJson)
                ? []
                : JsonSerializer.Deserialize<List<BpmAlternative>>(row.BpmAlternativesJson) ?? [];
            bpm = new BpmEstimate(b, row.BpmScore ?? 0, ParseLevel(row.BpmLevel), alternatives, row.BpmFlags ?? "");
        }
        KeyEstimate? key = null;
        if (row.KeyStatus == "Estimated" && row.KeyEstimate is { } k)
            key = new KeyEstimate(k, row.KeyScore ?? 0, ParseLevel(row.KeyLevel), row.KeyRunner, row.KeyFlags ?? "", row.TuningCents ?? 0);

        return new TrackAnalysis(trackId, (int)row.AnalyzerVersion, bpm, key, row.AnalyzedSeconds, row.Decoder, row.Message, DapperConfig.FromDb(row.AnalyzedUtc));
    }

    public Task PutAsync(TrackAnalysis a, long fileSize, DateTime modifiedUtc, CancellationToken ct) =>
        _writes.EnqueueAsync((conn, tx) => conn.Execute(
            @"INSERT INTO TrackAnalysis(TrackId, FileSize, ModifiedUtc, AnalyzerVersion, BpmStatus, BpmEstimate, BpmScore, BpmLevel, BpmFlags, BpmAlternativesJson,
                  KeyStatus, KeyEstimate, KeyScore, KeyLevel, KeyRunner, KeyFlags, TuningCents, AnalyzedSeconds, Decoder, Message, AnalyzedUtc)
              VALUES (@TrackId, @FileSize, @Modified, @Version, @BpmStatus, @Bpm, @BpmScore, @BpmLevel, @BpmFlags, @BpmAlt,
                  @KeyStatus, @Key, @KeyScore, @KeyLevel, @KeyRunner, @KeyFlags, @Tuning, @Seconds, @Decoder, @Message, @Analyzed)
              ON CONFLICT(TrackId) DO UPDATE SET FileSize = excluded.FileSize, ModifiedUtc = excluded.ModifiedUtc, AnalyzerVersion = excluded.AnalyzerVersion,
                  BpmStatus = excluded.BpmStatus, BpmEstimate = excluded.BpmEstimate, BpmScore = excluded.BpmScore, BpmLevel = excluded.BpmLevel,
                  BpmFlags = excluded.BpmFlags, BpmAlternativesJson = excluded.BpmAlternativesJson, KeyStatus = excluded.KeyStatus,
                  KeyEstimate = excluded.KeyEstimate, KeyScore = excluded.KeyScore, KeyLevel = excluded.KeyLevel, KeyRunner = excluded.KeyRunner,
                  KeyFlags = excluded.KeyFlags, TuningCents = excluded.TuningCents, AnalyzedSeconds = excluded.AnalyzedSeconds, Decoder = excluded.Decoder,
                  Message = excluded.Message, AnalyzedUtc = excluded.AnalyzedUtc",
            new
            {
                a.TrackId, FileSize = fileSize, Modified = DapperConfig.ToDb(modifiedUtc), Version = a.AnalyzerVersion,
                BpmStatus = a.Bpm is null ? "NoEstimate" : "Estimated", Bpm = a.Bpm?.Bpm, BpmScore = a.Bpm?.Score, BpmLevel = a.Bpm?.Level.ToString(),
                BpmFlags = a.Bpm?.Flags, BpmAlt = a.Bpm is null ? null : JsonSerializer.Serialize(a.Bpm.Alternatives),
                KeyStatus = a.Key is null ? "NoEstimate" : "Estimated", Key = a.Key?.Camelot, KeyScore = a.Key?.Score, KeyLevel = a.Key?.Level.ToString(),
                KeyRunner = a.Key?.Runner, KeyFlags = a.Key?.Flags, Tuning = a.Key?.TuningCents, Seconds = a.AnalyzedSeconds, a.Decoder, a.Message,
                Analyzed = DapperConfig.ToDb(a.AnalyzedUtc)
            }, tx), ct);

    public Task RebindAsync(long trackId, long newSize, DateTime newModifiedUtc, CancellationToken ct) =>
        _writes.EnqueueAsync((conn, tx) => conn.Execute(
            "UPDATE TrackAnalysis SET FileSize = @newSize, ModifiedUtc = @modified WHERE TrackId = @trackId",
            new { trackId, newSize, modified = DapperConfig.ToDb(newModifiedUtc) }, tx), ct);

    public async Task<IReadOnlyList<long>> FindCandidatesAsync(AnalysisScope scope, CancellationToken ct)
    {
        const string Missing = "(t.Bpm IS NULL OR t.Bpm <= 0 OR t.MusicalKey IS NULL OR TRIM(t.MusicalKey) = '')";
        var sql = scope.FolderId is null
            ? $"SELECT t.Id FROM Tracks t WHERE t.Missing = 0 AND {Missing} ORDER BY t.Id LIMIT @limit"
            : scope.Recursive
                ? $@"WITH RECURSIVE sub(Id) AS (SELECT @folder UNION ALL SELECT f.Id FROM Folders f JOIN sub s ON f.ParentId = s.Id)
                     SELECT t.Id FROM Tracks t WHERE t.Missing = 0 AND {Missing} AND t.FolderId IN (SELECT Id FROM sub) ORDER BY t.Id LIMIT @limit"
                : $"SELECT t.Id FROM Tracks t WHERE t.Missing = 0 AND {Missing} AND t.FolderId = @folder ORDER BY t.Id LIMIT @limit";
        using var conn = _factory.Open();
        return (await conn.QueryAsync<long>(sql, new { folder = scope.FolderId, limit = Math.Clamp(scope.Limit, 1, 100_000) })).ToList();
    }

    public async Task<int> CountAsync(CancellationToken ct)
    {
        using var conn = _factory.Open();
        return await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM TrackAnalysis");
    }

    public Task ClearAsync(CancellationToken ct) =>
        _writes.EnqueueAsync((conn, tx) => conn.Execute("DELETE FROM TrackAnalysis", transaction: tx), ct);

    private static Confidence ParseLevel(string? s) => Enum.TryParse<Confidence>(s, out var level) ? level : Confidence.Low;
}
