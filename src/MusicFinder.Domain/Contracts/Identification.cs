using MusicFinder.Domain.Models;

namespace MusicFinder.Domain.Contracts;

/// <summary>
/// Looks a track up online (MusicBrainz, and AcoustID when an audio fingerprint is available). Opt-in: every entry point
/// returns <see cref="IdentificationOutcome.Disabled"/> without touching the network until the user has agreed.
/// It only proposes; writing tags goes through <see cref="ITagEditingService"/> after the user's preview and confirmation.
/// </summary>
public interface IIdentificationService
{
    /// <summary>Automatic method, cache allowed. Empty when nothing matched or nothing is ready.</summary>
    Task<IReadOnlyList<IdentificationCandidate>> IdentifyAsync(long trackId, CancellationToken ct);
    Task<IdentificationResult> IdentifyAsync(IdentificationRequest request, CancellationToken ct);
    /// <summary>One release with its track positions (one MusicBrainz lookup, cached). Null when it cannot be fetched.</summary>
    Task<ReleaseDetails?> GetReleaseDetailsAsync(string releaseId, CancellationToken ct);
    IdentificationReadiness GetReadiness();
    Task ClearCacheAsync(CancellationToken ct);
}

/// <summary>Computes an acoustic fingerprint with Chromaprint's fpcalc, which the user supplies.</summary>
public interface IFingerprintService
{
    /// <summary>Where fpcalc was found, or null.</summary>
    FingerprinterInfo? Locate();
    Task<FingerprintResult> ComputeAsync(string path, CancellationToken ct);
}

public interface IAcoustIdClient
{
    Task<IReadOnlyList<AcoustIdMatch>> LookupAsync(string apiKey, string fingerprint, int durationSeconds, CancellationToken ct);
}

/// <param name="Score">0..1 as reported by AcoustID.</param>
public sealed record AcoustIdMatch(string AcoustId, double Score, IReadOnlyList<string> RecordingIds);

public interface IMusicBrainzClient
{
    Task<MbRecording?> GetRecordingAsync(string recordingId, CancellationToken ct);
    Task<MbRelease?> GetReleaseAsync(string releaseId, CancellationToken ct);
    Task<IReadOnlyList<MbSearchHit>> SearchRecordingsAsync(string luceneQuery, int limit, CancellationToken ct);
}

public sealed record MbRecording(
    string Id, string Title, string ArtistCredit, long? DurationMs,
    IReadOnlyList<ReleaseSummary> Releases, IReadOnlyList<string> Genres, IReadOnlyList<string> Isrcs);

public sealed record MbRelease(
    string Id, string Title, string? Date, string? Country, string AlbumArtistCredit,
    IReadOnlyList<string> Labels, IReadOnlyList<ReleaseTrack> Tracks);

/// <param name="Score">0..100 as reported by MusicBrainz's search.</param>
public sealed record MbSearchHit(string RecordingId, int Score, MbRecording Recording);

/// <summary>Remembered fingerprints, lookups and MusicBrainz records, so the same question is never asked twice.</summary>
public interface IIdentificationCache
{
    Task<CachedFingerprint?> GetFingerprintAsync(long trackId, long fileSize, DateTime modifiedUtc, CancellationToken ct);
    Task PutFingerprintAsync(long trackId, long fileSize, DateTime modifiedUtc, FingerprintResult fingerprint, DateTime nowUtc, CancellationToken ct);
    /// <summary>Null when missing or past its expiry.</summary>
    Task<CachedLookup?> GetLookupAsync(string requestKey, DateTime nowUtc, CancellationToken ct);
    Task PutLookupAsync(string requestKey, long trackId, IdentificationMethod method, LookupOutcome outcome, string resultJson,
        DateTime nowUtc, DateTime expiresUtc, CancellationToken ct);
    /// <summary>Raw JSON of a MusicBrainz "recording" or "release", null when missing or expired.</summary>
    Task<string?> GetEntityAsync(string entityType, string mbid, DateTime nowUtc, CancellationToken ct);
    Task PutEntityAsync(string entityType, string mbid, string json, DateTime nowUtc, DateTime expiresUtc, CancellationToken ct);
    Task<int> CountAsync(CancellationToken ct);
    Task ClearAsync(CancellationToken ct);
}

/// <summary>
/// Fetches the front cover of a MusicBrainz release from the Cover Art Archive. Only on request, never stored in the
/// database; returns null when the release has no cover, the service is off, or the image is not a usable JPEG or PNG.
/// </summary>
public interface ICoverArtClient
{
    Task<CoverImage?> GetFrontAsync(string releaseId, CancellationToken ct);
}
