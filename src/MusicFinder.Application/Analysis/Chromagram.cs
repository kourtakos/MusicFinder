namespace MusicFinder.Application.Analysis;

/// <summary>How much of each pitch class (C, C#, ... B) is in a clip, and how far its tuning is from concert pitch.</summary>
/// <param name="Values">Twelve weights, index 0 = C.</param>
/// <param name="TuningCents">The clip's pitch relative to A = 440 Hz equal temperament, in cents (-50..+50).</param>
public sealed record Chroma(double[] Values, double TuningCents, int PeakCount)
{
    public bool IsEmpty => PeakCount == 0;
}

/// <summary>
/// A chromagram made from spectral peaks: in each frame the prominent peaks between 55 and 1760 Hz are located precisely
/// (parabolic interpolation), the clip's tuning is taken from how those peaks sit relative to the semitone grid, and every peak then
/// counts for the nearest pitch class. Using peaks rather than all the energy keeps noise and drums out of the harmony.
/// </summary>
public static class Chromagram
{
    public const int FrameSize = 4096;
    public const int Hop = 2048;
    public const double LowHz = 55;
    public const double HighHz = 1760;
    private const double PeakFloor = 0.05;

    public static Chroma Compute(ReadOnlySpan<float> audio, int sampleRate)
    {
        var empty = new Chroma(new double[12], 0, 0);
        if (audio.Length < FrameSize + Hop) return empty;

        var fft = new Fft(FrameSize);
        var window = Fft.Hann(FrameSize);
        var mags = new double[FrameSize / 2 + 1];
        var binHz = (double)sampleRate / FrameSize;
        var lo = Math.Max(2, (int)Math.Ceiling(LowHz / binHz));
        var hi = Math.Min(FrameSize / 2 - 2, (int)Math.Floor(HighHz / binHz));

        var peaks = new List<(double Midi, double Weight)>();
        for (var start = 0; start + FrameSize <= audio.Length; start += Hop)
        {
            fft.Magnitudes(audio.Slice(start, FrameSize), window, mags);
            var frameMax = 0.0;
            for (var b = lo; b <= hi; b++) frameMax = Math.Max(frameMax, mags[b]);
            if (frameMax < 1e-6) continue;                                    // a silent frame says nothing about the key

            for (var b = lo; b <= hi; b++)
            {
                var m = mags[b];
                if (m <= mags[b - 1] || m < mags[b + 1] || m < PeakFloor * frameMax) continue;
                var a = mags[b - 1];
                var c = mags[b + 1];
                var denominator = a - 2 * m + c;
                var shift = denominator == 0 ? 0 : 0.5 * (a - c) / denominator;
                var frequency = (b + shift) * binHz;
                peaks.Add((69 + 12 * Math.Log2(frequency / 440.0), Math.Sqrt(m / frameMax)));
            }
        }
        if (peaks.Count == 0) return empty;

        // Tuning: a circular average of the distances to the nearest semitone (the distances wrap around at half a semitone).
        double sin = 0, cos = 0;
        foreach (var (midi, weight) in peaks)
        {
            var angle = 2 * Math.PI * (midi - Math.Round(midi));
            sin += weight * Math.Sin(angle);
            cos += weight * Math.Cos(angle);
        }
        var tuning = Math.Atan2(sin, cos) / (2 * Math.PI);                    // semitones, -0.5..0.5

        var chroma = new double[12];
        foreach (var (midi, weight) in peaks)
        {
            var pitchClass = (((int)Math.Round(midi - tuning)) % 12 + 12) % 12;
            chroma[pitchClass] += weight;
        }
        return new Chroma(chroma, Math.Round(tuning * 100, 1), peaks.Count);
    }
}
