using Dapper;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.Infrastructure.Persistence.Repositories;

/// <summary>
/// Remembers fingerprints (per file identity), online lookups (per question) and MusicBrainz records (per id).
/// Times are written with <see cref="DapperConfig.ToDb"/>, a fixed-width ISO form, so "expires after now" is a plain text comparison.
/// </summary>
public sealed class IdentificationCacheRepository : IIdentificationCache
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _writes;

    public IdentificationCacheRepository(SqliteConnectionFactory factory, SqliteWriteQueue writes)
    {
        _factory = factory;
        _writes = writes;
    }

    private sealed record FingerprintRow(long FileSize, string ModifiedUtc, string Fingerprint, long DurationSeconds, long Algorithm, long LengthSeconds, string ComputedUtc);
    private sealed record LookupRow(string RequestKey, string Method, string Outcome, string ResultJson, string CheckedUtc);

    public async Task<CachedFingerprint?> GetFingerprintAsync(long trackId, long fileSize, DateTime modifiedUtc, CancellationToken ct)
    {
        using var conn = _factory.Open();
        var row = await conn.QuerySingleOrDefaultAsync<FingerprintRow>(
            @"SELECT FileSize, ModifiedUtc, Fingerprint, DurationSeconds, Algorithm, LengthSeconds, ComputedUtc
              FROM IdentificationCache WHERE TrackId = @trackId", new { trackId });
        // The file is the same only if both its size and its modified time are what they were when it was fingerprinted.
        if (row is null || row.FileSize != fileSize || DapperConfig.FromDb(row.ModifiedUtc) != UtcTime.Normalize(modifiedUtc)) return null;
        return new CachedFingerprint(row.Fingerprint, (int)row.DurationSeconds, (int)row.Algorithm, (int)row.LengthSeconds, DapperConfig.FromDb(row.ComputedUtc));
    }

    public Task PutFingerprintAsync(long trackId, long fileSize, DateTime modifiedUtc, FingerprintResult fingerprint, DateTime nowUtc, CancellationToken ct) =>
        _writes.EnqueueAsync((conn, tx) => conn.Execute(
            @"INSERT INTO IdentificationCache(TrackId, FileSize, ModifiedUtc, Fingerprint, DurationSeconds, Algorithm, LengthSeconds, ComputedUtc)
              VALUES (@trackId, @fileSize, @modified, @fp, @duration, @algorithm, @length, @now)
              ON CONFLICT(TrackId) DO UPDATE SET FileSize = excluded.FileSize, ModifiedUtc = excluded.ModifiedUtc,
                Fingerprint = excluded.Fingerprint, DurationSeconds = excluded.DurationSeconds, Algorithm = excluded.Algorithm,
                LengthSeconds = excluded.LengthSeconds, ComputedUtc = excluded.ComputedUtc",
            new
            {
                trackId, fileSize, modified = DapperConfig.ToDb(modifiedUtc), fp = fingerprint.Fingerprint,
                duration = fingerprint.DurationSeconds, algorithm = fingerprint.Algorithm, length = fingerprint.LengthSeconds,
                now = DapperConfig.ToDb(nowUtc)
            }, tx), ct);

    public async Task<CachedLookup?> GetLookupAsync(string requestKey, DateTime nowUtc, CancellationToken ct)
    {
        using var conn = _factory.Open();
        var row = await conn.QuerySingleOrDefaultAsync<LookupRow>(
            @"SELECT RequestKey, Method, Outcome, ResultJson, CheckedUtc FROM IdentificationLookups
              WHERE RequestKey = @requestKey AND ExpiresUtc > @now", new { requestKey, now = DapperConfig.ToDb(nowUtc) });
        return row is null
            ? null
            : new CachedLookup(row.RequestKey, Enum.Parse<IdentificationMethod>(row.Method), Enum.Parse<LookupOutcome>(row.Outcome),
                row.ResultJson, DapperConfig.FromDb(row.CheckedUtc));
    }

    public Task PutLookupAsync(string requestKey, long trackId, IdentificationMethod method, LookupOutcome outcome, string resultJson,
        DateTime nowUtc, DateTime expiresUtc, CancellationToken ct) =>
        _writes.EnqueueAsync((conn, tx) => conn.Execute(
            @"INSERT INTO IdentificationLookups(RequestKey, TrackId, Method, Outcome, ResultJson, CheckedUtc, ExpiresUtc)
              VALUES (@requestKey, @trackId, @method, @outcome, @resultJson, @now, @expires)
              ON CONFLICT(RequestKey) DO UPDATE SET TrackId = excluded.TrackId, Method = excluded.Method, Outcome = excluded.Outcome,
                ResultJson = excluded.ResultJson, CheckedUtc = excluded.CheckedUtc, ExpiresUtc = excluded.ExpiresUtc",
            new
            {
                requestKey, trackId, method = method.ToString(), outcome = outcome.ToString(), resultJson,
                now = DapperConfig.ToDb(nowUtc), expires = DapperConfig.ToDb(expiresUtc)
            }, tx), ct);

    public async Task<string?> GetEntityAsync(string entityType, string mbid, DateTime nowUtc, CancellationToken ct)
    {
        using var conn = _factory.Open();
        return await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT Json FROM MusicBrainzCache WHERE EntityType = @entityType AND Mbid = @mbid AND ExpiresUtc > @now",
            new { entityType, mbid = mbid.ToLowerInvariant(), now = DapperConfig.ToDb(nowUtc) });
    }

    public Task PutEntityAsync(string entityType, string mbid, string json, DateTime nowUtc, DateTime expiresUtc, CancellationToken ct) =>
        _writes.EnqueueAsync((conn, tx) => conn.Execute(
            @"INSERT INTO MusicBrainzCache(EntityType, Mbid, Json, FetchedUtc, ExpiresUtc)
              VALUES (@entityType, @mbid, @json, @now, @expires)
              ON CONFLICT(EntityType, Mbid) DO UPDATE SET Json = excluded.Json, FetchedUtc = excluded.FetchedUtc, ExpiresUtc = excluded.ExpiresUtc",
            new { entityType, mbid = mbid.ToLowerInvariant(), json, now = DapperConfig.ToDb(nowUtc), expires = DapperConfig.ToDb(expiresUtc) }, tx), ct);

    public async Task<int> CountAsync(CancellationToken ct)
    {
        using var conn = _factory.Open();
        return await conn.ExecuteScalarAsync<int>(
            @"SELECT (SELECT COUNT(*) FROM IdentificationCache) + (SELECT COUNT(*) FROM IdentificationLookups) + (SELECT COUNT(*) FROM MusicBrainzCache)");
    }

    public Task ClearAsync(CancellationToken ct) =>
        _writes.EnqueueAsync((conn, tx) =>
        {
            conn.Execute("DELETE FROM IdentificationCache", transaction: tx);
            conn.Execute("DELETE FROM IdentificationLookups", transaction: tx);
            conn.Execute("DELETE FROM MusicBrainzCache", transaction: tx);
        }, ct);
}
