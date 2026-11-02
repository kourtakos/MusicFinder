using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Analysis;

/// <summary>
/// Estimates the tempo of a clip. This is a heuristic: it finds how regularly sound starts (autocorrelation of the onset envelope,
/// reinforced at two and four beats), prefers tempos a DJ would play (a soft preference for about 70-180 BPM), then refines the
/// beat period against the whole clip. The answer always comes with the half and double tempo and a confidence, and a clip whose tempo
/// drifts, or that has no steady pulse, gets a low confidence or no estimate at all.
/// </summary>
public static class TempoEstimator
{
    public const double MinBpm = 55;
    public const double MaxBpm = 210;
    public const double DjLow = 70;
    public const double DjHigh = 180;
    public const double PriorCentre = 120;
    public const double PriorSigmaOctaves = 0.9;

    /// <summary>A clip shorter than this has too little rhythm to measure.</summary>
    public const double MinSeconds = 8;
    /// <summary>Below this many seconds the tempo cannot be checked for drift, so confidence is capped.</summary>
    public const double DriftCheckSeconds = 24;

    private const double ScanStep = 0.1;
    private const double MinStrength = 0.12;

    public static BpmEstimate? Estimate(ReadOnlySpan<float> audio, int sampleRate) => Estimate(OnsetEnvelope.Compute(audio, sampleRate));

    public static BpmEstimate? Estimate(Onsets onsets)
    {
        var env = onsets.Envelope;
        if (onsets.Seconds < MinSeconds) return null;

        var maxLag = (int)Math.Ceiling(4 * onsets.FrameRate * 60 / MinBpm) + 2;
        if (env.Length < maxLag * 2) return null;
        var acf = Autocorrelation(env, maxLag);
        if (acf is null) return null;                                        // no variation at all: silence or a constant tone

        var scan = Scan(acf, onsets.FrameRate);
        var best = scan.OrderByDescending(p => p.Weighted).First();
        if (best.Raw < MinStrength) return null;                             // no steady pulse

        var bpm = Refine(env, onsets.FrameRate, best.Bpm);

        // Prominence: how far the best tempo stands above every unrelated tempo (not its octaves or simple ratios).
        var competitor = scan.Where(p => !IsRelated(p.Bpm, best.Bpm)).Select(p => p.Weighted).DefaultIfEmpty(0).Max();
        var prominence = Math.Clamp(1 - competitor / best.Weighted, 0, 1);
        var strength = Math.Clamp(best.Raw, 0, 1);
        var score = prominence * Math.Sqrt(strength);

        var alternatives = new List<BpmAlternative>();
        var ambiguous = false;
        foreach (var (factor, label) in new[] { (2.0, "double"), (0.5, "half") })
        {
            var alt = bpm * factor;
            if (alt < MinBpm * 0.9 || alt > MaxBpm * 1.3) continue;
            var rel = Raw(acf, onsets.FrameRate, alt) / Math.Max(1e-9, Raw(acf, onsets.FrameRate, bpm));
            alternatives.Add(new BpmAlternative(Math.Round(alt, 1), label, Math.Round(rel, 3)));
            if (alt is >= DjLow and <= DjHigh && rel >= 0.9) ambiguous = true;
        }

        var flags = new List<string>();
        var level = LevelOf(score);
        if (ambiguous) { flags.Add("octave-ambiguous"); level = Lower(level); }

        var drift = CheckDrift(env, onsets.FrameRate, bpm);
        if (drift == Drift.Unstable) { flags.Add("unstable"); level = Confidence.Low; }
        else if (drift == Drift.TooShort && level > Confidence.Medium) level = Confidence.Medium;

        if (level == Confidence.None) return null;
        return new BpmEstimate(Math.Round(bpm, 1), Math.Round(score, 3), level, alternatives, string.Join(' ', flags));
    }

    // ===== autocorrelation and the comb score =====

    private readonly record struct ScanPoint(double Bpm, double Raw, double Weighted);

    /// <summary>Normalised autocorrelation of the mean-removed envelope for lags 0..maxLag; null when the envelope is flat.</summary>
    internal static double[]? Autocorrelation(double[] x, int maxLag)
    {
        var mean = x.Average();
        var centred = new double[x.Length];
        double energy = 0;
        for (var i = 0; i < x.Length; i++) { centred[i] = x[i] - mean; energy += centred[i] * centred[i]; }
        if (energy < 1e-12) return null;

        var r = new double[maxLag + 1];
        for (var lag = 0; lag <= maxLag; lag++)
        {
            double sum = 0;
            for (var i = 0; i + lag < centred.Length; i++) sum += centred[i] * centred[i + lag];
            r[lag] = sum / energy * (centred.Length / (double)(centred.Length - lag));      // undo the shrinking overlap at long lags
        }
        return r;
    }

    private static double At(double[] acf, double lag)
    {
        if (lag < 0 || lag >= acf.Length - 1) return 0;
        var i = (int)lag;
        var frac = lag - i;
        return Math.Max(0, acf[i] * (1 - frac) + acf[i + 1] * frac);
    }

    /// <summary>The pulse strength at a tempo: the autocorrelation at one, two and four beats, the longer ones counting less.</summary>
    internal static double Raw(double[] acf, double frameRate, double bpm)
    {
        var lag = frameRate * 60 / bpm;
        return At(acf, lag) + 0.5 * At(acf, 2 * lag) + 0.25 * At(acf, 4 * lag);
    }

    /// <summary>A soft preference for tempos that are played in practice, centred on 120 BPM.</summary>
    internal static double Prior(double bpm)
    {
        var octaves = Math.Log2(bpm / PriorCentre) / PriorSigmaOctaves;
        return Math.Exp(-0.5 * octaves * octaves);
    }

    private static List<ScanPoint> Scan(double[] acf, double frameRate, double step = ScanStep)
    {
        var points = new List<ScanPoint>();
        for (var bpm = MinBpm; bpm <= MaxBpm; bpm += step)
        {
            var raw = Raw(acf, frameRate, bpm);
            points.Add(new ScanPoint(bpm, raw, raw * Prior(bpm)));
        }
        return points;
    }

    /// <summary>The same musical tempo, an octave away, or a simple ratio of it (3:2): not a rival interpretation.</summary>
    private static bool IsRelated(double bpm, double best)
    {
        var octaves = Math.Log2(bpm / best);
        foreach (var target in new[] { 0.0, 1.0, -1.0, 0.585, -0.585 })
            if (Math.Abs(octaves - target) < 0.07) return true;
        return false;
    }

    // ===== refining against the whole clip =====

    /// <summary>
    /// The autocorrelation is blurred by about a frame, so the beat period is refined: for periods within 1.5 % of the coarse one, the
    /// onset envelope is sampled along an evenly spaced beat grid (best phase), and the period whose grid lands on the strongest onsets wins.
    /// </summary>
    internal static double Refine(double[] env, double frameRate, double coarseBpm)
    {
        var coarsePeriod = frameRate * 60 / coarseBpm;                       // in frames
        var bestPeriod = coarsePeriod;
        var bestScore = double.MinValue;
        for (var ratio = 0.985; ratio <= 1.015 + 1e-9; ratio += 0.0005)
        {
            var period = coarsePeriod * ratio;
            var score = GridScore(env, period);
            if (score > bestScore) { bestScore = score; bestPeriod = period; }
        }
        return frameRate * 60 / bestPeriod;
    }

    /// <summary>The mean onset strength along a beat grid with this period (in frames), at the best starting phase.</summary>
    private static double GridScore(double[] env, double period)
    {
        var best = 0.0;
        var phases = Math.Max(1, (int)Math.Ceiling(period));
        for (var p = 0; p < phases; p++)
        {
            double sum = 0;
            var beats = 0;
            for (var pos = p + 0.0; pos < env.Length - 1; pos += period)
            {
                var i = (int)pos;
                var frac = pos - i;
                // The strongest of the neighbouring frames: an onset smeared across two frames still counts in full.
                sum += Math.Max(env[i] * (1 - frac) + env[i + 1] * frac, Math.Max(env[i], env[i + 1]) * 0.85);
                beats++;
            }
            if (beats > 0) best = Math.Max(best, sum / beats);
        }
        return best;
    }

    // ===== does the tempo hold throughout? =====

    private enum Drift { Steady, Unstable, TooShort }

    /// <summary>Estimates the tempo again in separate parts of the clip; parts that disagree (octaves aside) mean the tempo moves.</summary>
    private static Drift CheckDrift(double[] env, double frameRate, double bpm)
    {
        var seconds = env.Length / frameRate;
        if (seconds < DriftCheckSeconds) return Drift.TooShort;

        var parts = seconds >= 60 ? 3 : 2;
        var length = env.Length / parts;
        var maxLag = (int)Math.Ceiling(4 * frameRate * 60 / MinBpm) + 2;
        var estimates = new List<double>();
        for (var i = 0; i < parts; i++)
        {
            var part = env.AsSpan(i * length, length).ToArray();
            var acf = Autocorrelation(part, maxLag);
            if (acf is null) return Drift.Unstable;
            var top = Scan(acf, frameRate, 0.25).OrderByDescending(p => p.Weighted).First();
            estimates.Add(Refine(part, frameRate, top.Bpm));
        }

        foreach (var e in estimates)
        {
            var ratio = e / bpm;
            var nearest = Math.Round(Math.Log2(ratio));                       // octaves apart: counts as the same tempo
            var off = Math.Abs(ratio / Math.Pow(2, nearest) - 1);
            if (off > 0.02) return Drift.Unstable;
        }
        return Drift.Steady;
    }

    // ===== confidence =====

    /// <summary>Thresholds set against synthetic rhythms of known tempo; see the tests.</summary>
    internal static Confidence LevelOf(double score) =>
        score >= 0.55 ? Confidence.High : score >= 0.35 ? Confidence.Medium : score >= 0.18 ? Confidence.Low : Confidence.None;

    private static Confidence Lower(Confidence c) => c switch { Confidence.High => Confidence.Medium, Confidence.Medium => Confidence.Low, _ => c };
}
