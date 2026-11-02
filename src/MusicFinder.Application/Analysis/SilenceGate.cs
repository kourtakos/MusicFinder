namespace MusicFinder.Application.Analysis;

/// <summary>What is audible in a clip: how many seconds, and where the audible part starts and ends.</summary>
public sealed record AudibleSpan(double ActiveSeconds, int StartSample, int EndSample)
{
    public int Length => Math.Max(0, EndSample - StartSample);
}

/// <summary>
/// Finds the audible part of a clip so a long silent intro or outro does not dilute the analysis, and tells a silent file from
/// music. Frames quieter than -60 dBFS count as silence.
/// </summary>
public static class SilenceGate
{
    public const double ThresholdDb = -60;
    public const double FrameSeconds = 0.05;

    public static AudibleSpan Measure(ReadOnlySpan<float> audio, int sampleRate)
    {
        var frame = Math.Max(1, (int)(sampleRate * FrameSeconds));
        var threshold = Math.Pow(10, ThresholdDb / 20);
        var first = -1;
        var last = -1;
        var active = 0;
        for (var start = 0; start + frame <= audio.Length; start += frame)
        {
            double sum = 0;
            for (var i = 0; i < frame; i++) sum += (double)audio[start + i] * audio[start + i];
            if (Math.Sqrt(sum / frame) < threshold) continue;
            active++;
            if (first < 0) first = start;
            last = start + frame;
        }
        return first < 0 ? new AudibleSpan(0, 0, 0) : new AudibleSpan(active * FrameSeconds, first, last);
    }
}
