using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Entities;
using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Identification;

/// <summary>
/// Looks a track up online and returns candidates to choose from. It proposes only: nothing is written here, and nothing
/// goes to the network unless the user turned identification on and saw what is sent. Failures come back as an outcome with a
/// message, not as exceptions (except cancellation, which is reported as <see cref="IdentificationOutcome.Cancelled"/>).
/// </summary>
public sealed class IdentificationService : IIdentificationService
{
    public static readonly TimeSpan FoundLifetime = TimeSpan.FromDays(30);
    public static readonly TimeSpan NoMatchLifetime = TimeSpan.FromDays(7);
    public const int MaxFingerprintCandidates = 5;
    public const int SearchLimit = 10;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ITrackRepository _tracks;
    private readonly IIdentificationCache _cache;
    private readonly IFingerprintService _fingerprints;
    private readonly IAcoustIdClient _acoustId;
    private readonly IMusicBrainzClient _musicBrainz;
    private readonly ISettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<IdentificationService>? _log;

    public IdentificationService(ITrackRepository tracks, IIdentificationCache cache, IFingerprintService fingerprints, IAcoustIdClient acoustId,
        IMusicBrainzClient musicBrainz, ISettingsStore settings, TimeProvider time, ILogger<IdentificationService>? log = null)
    {
        _tracks = tracks;
        _cache = cache;
        _fingerprints = fingerprints;
        _acoustId = acoustId;
        _musicBrainz = musicBrainz;
        _settings = settings;
        _time = time;
        _log = log;
    }

    public IdentificationReadiness GetReadiness()
    {
        var s = _settings.Load();
        return new IdentificationReadiness(s.IdentificationEnabled, s.IdentificationConsentUtc is not null,
            !string.IsNullOrWhiteSpace(s.AcoustIdApiKey), !string.IsNullOrWhiteSpace(s.MusicBrainzContact), _fingerprints.Locate());
    }

    public async Task<IReadOnlyList<IdentificationCandidate>> IdentifyAsync(long trackId, CancellationToken ct) =>
        (await IdentifyAsync(new IdentificationRequest(trackId), ct)).Candidates;

    public async Task<IdentificationResult> IdentifyAsync(IdentificationRequest request, CancellationToken ct)
    {
        var requested = request.Method;
        try
        {
            var settings = _settings.Load();
            if (!settings.IdentificationEnabled || settings.IdentificationConsentUtc is null)
                return Fail(request, requested, IdentificationOutcome.Disabled,
                    "Online identification is off. Turn it on in Settings; you will see exactly what is sent first.");
            if (string.IsNullOrWhiteSpace(settings.MusicBrainzContact))
                return Fail(request, requested, IdentificationOutcome.NoContact,
                    "MusicBrainz asks every program to say who is calling. Add a contact e-mail in Settings.");

            ct.ThrowIfCancellationRequested();
            var track = await _tracks.GetAsync(request.TrackId, ct);
            if (track is null)
                return Fail(request, requested, IdentificationOutcome.Error, "That track is no longer in the library.");

            var method = requested == IdentificationMethod.Auto ? Choose(track) : requested;
            _log?.LogInformation("Identifying track {TrackId} by {Method}", track.Id, method);
            return method switch
            {
                IdentificationMethod.AcoustIdFingerprint => await ByFingerprintAsync(request, track, settings, ct),
                IdentificationMethod.ExistingRecordingId => await ByRecordingIdAsync(request, track, ct),
                _ => await ByTagsAsync(request, track, ct)
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Fail(request, requested, IdentificationOutcome.Cancelled, "Cancelled.");
        }
        catch (IdentificationHttpException ex)
        {
            _log?.LogWarning("Identification of track {TrackId} failed: {Failure} {Status}", request.TrackId, ex.Failure, ex.StatusCode);
            return Fail(request, requested, ex.Failure switch
            {
                IdentificationFailure.Offline => IdentificationOutcome.Offline,
                IdentificationFailure.RateLimited => IdentificationOutcome.RateLimited,
                _ => IdentificationOutcome.Error
            }, ex.Failure switch
            {
                IdentificationFailure.Offline => "Could not reach the identification service. Check your connection; your library and search keep working offline.",
                IdentificationFailure.RateLimited => "The service asked MusicFinder to slow down and did not recover in time. Try again in a minute.",
                _ => ex.Message
            });
        }
    }

    public async Task<ReleaseDetails?> GetReleaseDetailsAsync(string releaseId, CancellationToken ct)
    {
        var s = _settings.Load();
        if (!s.IdentificationEnabled || s.IdentificationConsentUtc is null || string.IsNullOrWhiteSpace(s.MusicBrainzContact)) return null;
        var release = await _musicBrainz.GetReleaseAsync(releaseId, ct);
        return release is null
            ? null
            : new ReleaseDetails(release.Id, release.Title, release.Date, release.Country, release.AlbumArtistCredit, release.Labels, release.Tracks);
    }

    public Task ClearCacheAsync(CancellationToken ct) => _cache.ClearAsync(ct);

    // ===== methods =====

    /// <summary>Fingerprint when it can run, else the id already in the file, else a search by tags.</summary>
    private IdentificationMethod Choose(Track track)
    {
        if (GetReadiness().CanFingerprint) return IdentificationMethod.AcoustIdFingerprint;
        return string.IsNullOrWhiteSpace(track.MusicBrainzRecordingId) ? IdentificationMethod.MusicBrainzTagSearch : IdentificationMethod.ExistingRecordingId;
    }

    private async Task<IdentificationResult> ByFingerprintAsync(IdentificationRequest request, Track track, AppSettings settings, CancellationToken ct)
    {
        const IdentificationMethod method = IdentificationMethod.AcoustIdFingerprint;
        if (string.IsNullOrWhiteSpace(settings.AcoustIdApiKey))
            return Fail(request, method, IdentificationOutcome.NoApiKey, "Audio fingerprint lookup needs an AcoustID API key (Settings). You can still search by tags.");
        if (_fingerprints.Locate() is null)
            return Fail(request, method, IdentificationOutcome.FingerprinterMissing,
                "fpcalc.exe was not found. Put it next to MusicFinder.exe or set its path in Settings. You can still search by tags.");

        var now = Now();
        var fingerprint = await _cache.GetFingerprintAsync(track.Id, track.FileSize, track.ModifiedUtc, ct);
        FingerprintResult fp;
        if (fingerprint is not null && fingerprint.LengthSeconds == Math.Clamp(settings.FingerprintLengthSeconds, 10, 600))
        {
            fp = new FingerprintResult(fingerprint.Fingerprint, fingerprint.DurationSeconds, fingerprint.Algorithm, fingerprint.LengthSeconds);
        }
        else
        {
            try { fp = await _fingerprints.ComputeAsync(track.FullPath, ct); }
            catch (FingerprintException ex)
            {
                return Fail(request, method, ex.Missing ? IdentificationOutcome.FingerprinterMissing : IdentificationOutcome.FingerprintFailed, ex.Message);
            }
            await _cache.PutFingerprintAsync(track.Id, track.FileSize, track.ModifiedUtc, fp, now, ct);
        }

        var key = RequestKey("acoustid", fp.Fingerprint, fp.DurationSeconds.ToString());
        IReadOnlyList<AcoustIdMatch>? matches = null;
        var fromCache = false;
        DateTime? checkedUtc = null;
        if (!request.ForceRefresh && await _cache.GetLookupAsync(key, now, ct) is { } cached)
        {
            matches = JsonSerializer.Deserialize<List<AcoustIdMatch>>(cached.ResultJson, Json) ?? [];
            fromCache = true;
            checkedUtc = cached.CheckedUtc;
        }
        matches ??= await LookupAcoustIdAsync(settings.AcoustIdApiKey!, fp, key, track.Id, now, ct);

        // One candidate per recording, with the best score AcoustID gave it.
        var scores = new Dictionary<string, double>();
        foreach (var m in matches)
            foreach (var id in m.RecordingIds)
                if (!scores.TryGetValue(id, out var s) || m.Score > s) scores[id] = m.Score;

        var candidates = new List<IdentificationCandidate>();
        foreach (var (id, score) in scores.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Take(MaxFingerprintCandidates))
        {
            var recording = await _musicBrainz.GetRecordingAsync(id, ct);
            if (recording is not null)
                candidates.Add(ToCandidate(track, recording, method, acoustIdScore: score, searchScore: null));
        }
        return Done(request, method, candidates, fromCache, checkedUtc ?? now,
            matches.Count == 0 ? "AcoustID does not know this recording." : "AcoustID knew the recording, but MusicBrainz has no usable record of it.");
    }

    private async Task<IReadOnlyList<AcoustIdMatch>> LookupAcoustIdAsync(string apiKey, FingerprintResult fp, string key, long trackId, DateTime now, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var matches = await _acoustId.LookupAsync(apiKey, fp.Fingerprint, fp.DurationSeconds, ct);
        await _cache.PutLookupAsync(key, trackId, IdentificationMethod.AcoustIdFingerprint,
            matches.Count > 0 ? LookupOutcome.Found : LookupOutcome.NoMatch, JsonSerializer.Serialize(matches, Json),
            now, now + (matches.Count > 0 ? FoundLifetime : NoMatchLifetime), ct);
        return matches;
    }

    private async Task<IdentificationResult> ByRecordingIdAsync(IdentificationRequest request, Track track, CancellationToken ct)
    {
        const IdentificationMethod method = IdentificationMethod.ExistingRecordingId;
        if (string.IsNullOrWhiteSpace(track.MusicBrainzRecordingId))
            return Fail(request, method, IdentificationOutcome.NotEnoughTags, "This file carries no MusicBrainz id. Try the fingerprint or a search by tags.");
        var recording = await _musicBrainz.GetRecordingAsync(track.MusicBrainzRecordingId, ct);
        var candidates = recording is null ? [] : new List<IdentificationCandidate> { ToCandidate(track, recording, method, null, null) };
        return Done(request, method, candidates, fromCache: false, Now(), "MusicBrainz does not know the id stored in this file.");
    }

    private async Task<IdentificationResult> ByTagsAsync(IdentificationRequest request, Track track, CancellationToken ct)
    {
        const IdentificationMethod method = IdentificationMethod.MusicBrainzTagSearch;
        var terms = MusicBrainzQueryBuilder.TermsFor(track);
        if (terms is null)
            return Fail(request, method, IdentificationOutcome.NotEnoughTags, "There is no title to search for. Add a title, or name the file \"Artist - Title\".");

        var now = Now();
        var query = MusicBrainzQueryBuilder.Build(terms);
        var key = RequestKey("mbsearch", query);
        IReadOnlyList<MbSearchHit>? hits = null;
        var fromCache = false;
        DateTime? checkedUtc = null;
        if (!request.ForceRefresh && await _cache.GetLookupAsync(key, now, ct) is { } cached)
        {
            hits = JsonSerializer.Deserialize<List<MbSearchHit>>(cached.ResultJson, Json) ?? [];
            fromCache = true;
            checkedUtc = cached.CheckedUtc;
        }
        if (hits is null)
        {
            ct.ThrowIfCancellationRequested();
            hits = await _musicBrainz.SearchRecordingsAsync(query, SearchLimit, ct);
            await _cache.PutLookupAsync(key, track.Id, method, hits.Count > 0 ? LookupOutcome.Found : LookupOutcome.NoMatch,
                JsonSerializer.Serialize(hits, Json), now, now + (hits.Count > 0 ? FoundLifetime : NoMatchLifetime), ct);
        }

        var candidates = hits.OrderByDescending(h => h.Score)
            .Select(h => ToCandidate(track, h.Recording, method, null, h.Score, terms)).ToList();
        return Done(request, method, candidates, fromCache, checkedUtc ?? now, "MusicBrainz found no recording for these tags.");
    }

    // ===== helpers =====

    private IdentificationCandidate ToCandidate(Track track, MbRecording recording, IdentificationMethod method, double? acoustIdScore, int? searchScore,
        TagSearchTerms? terms = null) =>
        new(recording.Id, recording.Title, recording.ArtistCredit, recording.DurationMs, method, acoustIdScore, searchScore,
            CandidateEvidence.Compute(track, recording, terms), recording.Releases, recording.Genres, recording.Isrcs);

    private static IdentificationResult Done(IdentificationRequest request, IdentificationMethod method, IReadOnlyList<IdentificationCandidate> candidates,
        bool fromCache, DateTime checkedUtc, string noMatchMessage) =>
        candidates.Count > 0
            ? new IdentificationResult(request.TrackId, method, IdentificationOutcome.Found, candidates, null, fromCache, checkedUtc)
            : new IdentificationResult(request.TrackId, method, IdentificationOutcome.NoMatch, [], noMatchMessage + " Try the other method, or edit the tags by hand.", fromCache, checkedUtc);

    private static IdentificationResult Fail(IdentificationRequest request, IdentificationMethod method, IdentificationOutcome outcome, string message) =>
        IdentificationResult.Failed(request.TrackId, method, outcome, message);

    private DateTime Now() => _time.GetUtcNow().UtcDateTime;

    /// <summary>A question's identity: the same fingerprint or the same query is the same question, whichever file asked.</summary>
    public static string RequestKey(params string[] parts) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(string.Join("\u001f", parts)))).ToLowerInvariant();
}
