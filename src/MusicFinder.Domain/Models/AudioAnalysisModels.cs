namespace MusicFinder.Domain.Models;

/// <summary>How much an estimate can be trusted. These are estimates from the audio, never facts.</summary>
public enum Confidence { None, Low, Medium, High }

/// <summary>How a musical key is written into the tag.</summary>
public enum KeyNotation { Camelot, OpenKey, NoteName }

/// <summary>What the analysis looks at: the tracks that lack a BPM or a key, optionally under one folder.</summary>
public sealed record AnalysisScope(long? FolderId = null, bool Recursive = true, int Limit = 5000);

/// <summary>Mono audio at the analysis sample rate, ready for the maths.</summary>
public sealed record DecodedAudio(float[] Mono, int SampleRate, double SourceDurationSeconds, int SourceSampleRate, int SourceChannels, string Decoder);

/// <param name="CentreWindow">Take the middle of the track (skipping intros and outros); otherwise start at <paramref name="StartSeconds"/>.</param>
public sealed record AudioDecodeRequest(int TargetSampleRate = 22050, double MaxSeconds = 90, bool CentreWindow = true, double StartSeconds = 0);

/// <summary>Either the audio, or why it could not be decoded (a message for the user).</summary>
public sealed record DecodeResult(DecodedAudio? Audio, string? Failure)
{
    public static DecodeResult Ok(DecodedAudio audio) => new(audio, null);
    public static DecodeResult Fail(string reason) => new(null, reason);
}

/// <summary>Another tempo that fits the music: half or double the estimate, with its relative strength.</summary>
public sealed record BpmAlternative(double Bpm, string Label, double RelativeScore);

/// <summary>An estimated tempo. <see cref="Flags"/> says what makes it doubtful ("octave-ambiguous", "unstable").</summary>
public sealed record BpmEstimate(double Bpm, double Score, Confidence Level, IReadOnlyList<BpmAlternative> Alternatives, string Flags)
{
    public bool IsReliable => Level >= Confidence.Medium && !Flags.Contains("unstable", StringComparison.Ordinal);
}

/// <summary>An estimated key, kept in Camelot form ("8A") and formatted for the tag when proposed.</summary>
public sealed record KeyEstimate(string Camelot, double Score, Confidence Level, string? Runner, string Flags, double TuningCents);

/// <summary>What analysing one file produced. A null estimate means "no estimate", which is a result (not an error).</summary>
public sealed record TrackAnalysis(
    long TrackId, int AnalyzerVersion, BpmEstimate? Bpm, KeyEstimate? Key, double AnalyzedSeconds, string Decoder,
    string? Message, DateTime AnalyzedUtc);

public enum AnalysisOutcome { Analyzed, FromCache, DecodeFailed, TooShort, Cancelled, NotFound, Error }

public sealed record AnalysisResult(long TrackId, AnalysisOutcome Outcome, TrackAnalysis? Analysis, string? Message)
{
    public static AnalysisResult Failed(long trackId, AnalysisOutcome outcome, string message) => new(trackId, outcome, null, message);
}
