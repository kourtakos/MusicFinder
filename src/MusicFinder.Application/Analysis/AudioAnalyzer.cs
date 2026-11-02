using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Analysis;

/// <summary>What analysing a decoded clip produced. A null estimate means "no estimate": the music gave no answer worth offering.</summary>
public sealed record AnalyzerOutput(BpmEstimate? Bpm, KeyEstimate? Key, double AnalyzedSeconds, string? Message);

/// <summary>
/// Runs the tempo and key estimators on a decoded clip after trimming the silence at its ends. Pure and deterministic: the same audio
/// always gives the same answer. Bump <see cref="Version"/> whenever the maths changes, so remembered estimates are made again.
/// </summary>
public static class AudioAnalyzer
{
    /// <summary>1 = the first analyzer (onset-flux tempo, peak chroma with Temperley profiles).</summary>
    public const int Version = 1;

    /// <summary>Less audible music than this cannot be analysed (a tempo needs several seconds of rhythm).</summary>
    public const double MinActiveSeconds = 8;

    public static AnalyzerOutput Analyze(DecodedAudio audio, KeyProfileSet profiles = KeyProfileSet.Combined)
    {
        var span = SilenceGate.Measure(audio.Mono, audio.SampleRate);
        if (span.ActiveSeconds < MinActiveSeconds)
        {
            return new AnalyzerOutput(null, null, span.Length / (double)audio.SampleRate,
                span.Length == 0 ? "The audio is silent." : "There is too little audible music to analyse.");
        }

        var music = audio.Mono.AsSpan(span.StartSample, span.Length);
        var bpm = TempoEstimator.Estimate(music, audio.SampleRate);
        var key = KeyEstimator.Estimate(music, audio.SampleRate, profiles);
        var seconds = span.Length / (double)audio.SampleRate;

        string? message = null;
        if (bpm is null && key is null) message = "No steady tempo and no clear key were found.";
        else if (bpm is null) message = "No steady tempo was found.";
        else if (key is null) message = "No clear key was found.";
        return new AnalyzerOutput(bpm, key, seconds, message);
    }
}
