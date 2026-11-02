using MusicFinder.Domain.Models;

namespace MusicFinder.Domain.Contracts;

/// <summary>Turns an audio file into mono samples. Never throws for an undecodable file: it says why (cancellation excepted).</summary>
public interface IAudioDecoder
{
    Task<DecodeResult> DecodeAsync(string path, AudioDecodeRequest request, CancellationToken ct);
}

/// <summary>
/// Estimates BPM and key from the audio itself. Estimates are stored apart from the tags and are written into a file only by
/// the user's explicit action, through the tag editing service.
/// </summary>
public interface IAudioAnalysisService
{
    /// <summary>Analyses one track (or returns the remembered answer for an unchanged file). <paramref name="force"/> ignores the memory.</summary>
    Task<AnalysisResult> AnalyzeAsync(long trackId, bool force, CancellationToken ct);
    /// <summary>The remembered analysis of a track, if its file is unchanged, without doing any work.</summary>
    Task<TrackAnalysis?> GetRememberedAsync(long trackId, CancellationToken ct);
    /// <summary>Ids of tracks that have no BPM or no key in their tags.</summary>
    Task<IReadOnlyList<long>> FindTracksNeedingAnalysisAsync(AnalysisScope scope, CancellationToken ct);
    /// <summary>After a tag write the audio is unchanged: keep the remembered analysis for the file's new size and time.</summary>
    Task RebindAfterWriteAsync(long trackId, long newSize, DateTime newModifiedUtc, CancellationToken ct);
    Task<int> CountAsync(CancellationToken ct);
    Task ClearAsync(CancellationToken ct);
}

public interface ITrackAnalysisStore
{
    /// <summary>The stored analysis, only while the file's size and time and the analyzer version are unchanged.</summary>
    Task<TrackAnalysis?> GetAsync(long trackId, long fileSize, DateTime modifiedUtc, int analyzerVersion, CancellationToken ct);
    Task PutAsync(TrackAnalysis analysis, long fileSize, DateTime modifiedUtc, CancellationToken ct);
    /// <summary>The file's size and time changed without its audio changing (a tag write): keep the row for the new identity.</summary>
    Task RebindAsync(long trackId, long newSize, DateTime newModifiedUtc, CancellationToken ct);
    Task<IReadOnlyList<long>> FindCandidatesAsync(AnalysisScope scope, CancellationToken ct);
    Task<int> CountAsync(CancellationToken ct);
    Task ClearAsync(CancellationToken ct);
}
