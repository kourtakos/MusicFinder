using Microsoft.Extensions.Logging;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Analysis;

/// <summary>
/// Analyses a track's audio for BPM and key and remembers the answer for as long as the file is unchanged. It never changes a tag or a
/// file: the estimates wait in their own table until the user writes them. Failures are returned as results (a message), not thrown;
/// only cancellation propagates.
/// </summary>
public sealed class AudioAnalysisService : IAudioAnalysisService
{
    /// <summary>A track shorter than this has too little rhythm or harmony to say anything.</summary>
    public const double MinTrackSeconds = 10;

    private readonly ITrackRepository _tracks;
    private readonly ITrackAnalysisStore _store;
    private readonly IAudioDecoder _decoder;
    private readonly ISettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<AudioAnalysisService>? _log;

    public AudioAnalysisService(ITrackRepository tracks, ITrackAnalysisStore store, IAudioDecoder decoder, ISettingsStore settings,
        TimeProvider time, ILogger<AudioAnalysisService>? log = null)
    {
        _tracks = tracks;
        _store = store;
        _decoder = decoder;
        _settings = settings;
        _time = time;
        _log = log;
    }

    public async Task<AnalysisResult> AnalyzeAsync(long trackId, bool force, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var track = await _tracks.GetAsync(trackId, ct);
        if (track is null) return AnalysisResult.Failed(trackId, AnalysisOutcome.NotFound, "That track is no longer in the library.");
        if (track.Missing) return AnalysisResult.Failed(trackId, AnalysisOutcome.DecodeFailed, "The file is missing, so it cannot be analysed.");

        if (!force)
        {
            var remembered = await _store.GetAsync(trackId, track.FileSize, track.ModifiedUtc, AudioAnalyzer.Version, ct);
            if (remembered is not null) return new AnalysisResult(trackId, AnalysisOutcome.FromCache, remembered, remembered.Message);
        }

        if (track.DurationMs is { } ms && ms < MinTrackSeconds * 1000)
            return AnalysisResult.Failed(trackId, AnalysisOutcome.TooShort, "The track is shorter than 10 seconds: too short to find a tempo or a key.");

        var seconds = Math.Clamp(_settings.Load().AnalysisSeconds, 30, 240);
        DecodeResult decoded;
        try { decoded = await _decoder.DecodeAsync(track.FullPath, new AudioDecodeRequest(MaxSeconds: seconds), ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        if (decoded.Audio is null)
        {
            _log?.LogInformation("Track {TrackId} could not be decoded: {Reason}", trackId, decoded.Failure);
            return AnalysisResult.Failed(trackId, AnalysisOutcome.DecodeFailed, decoded.Failure ?? "The file could not be decoded.");
        }

        // On the calling thread: the runner calls this from a low-priority worker, and the maths should keep that priority.
        var output = AudioAnalyzer.Analyze(decoded.Audio);
        var analysis = new TrackAnalysis(trackId, AudioAnalyzer.Version, output.Bpm, output.Key, output.AnalyzedSeconds, decoded.Audio.Decoder,
            output.Message, _time.GetUtcNow().UtcDateTime);
        await _store.PutAsync(analysis, track.FileSize, track.ModifiedUtc, ct);
        return new AnalysisResult(trackId, AnalysisOutcome.Analyzed, analysis, output.Message);
    }

    public async Task<TrackAnalysis?> GetRememberedAsync(long trackId, CancellationToken ct)
    {
        var track = await _tracks.GetAsync(trackId, ct);
        return track is null ? null : await _store.GetAsync(trackId, track.FileSize, track.ModifiedUtc, AudioAnalyzer.Version, ct);
    }

    public Task<IReadOnlyList<long>> FindTracksNeedingAnalysisAsync(AnalysisScope scope, CancellationToken ct) => _store.FindCandidatesAsync(scope, ct);

    public Task RebindAfterWriteAsync(long trackId, long newSize, DateTime newModifiedUtc, CancellationToken ct) =>
        _store.RebindAsync(trackId, newSize, newModifiedUtc, ct);

    public Task<int> CountAsync(CancellationToken ct) => _store.CountAsync(ct);

    public Task ClearAsync(CancellationToken ct) => _store.ClearAsync(ct);
}
