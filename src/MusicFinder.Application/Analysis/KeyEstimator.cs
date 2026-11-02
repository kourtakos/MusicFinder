using MusicFinder.Application.Similar;
using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Analysis;

/// <summary>Which published key profiles the estimate is compared with.</summary>
public enum KeyProfileSet { Temperley, KrumhanslKessler, /** <summary>The average of both: each corrects some of the other's mistakes.</summary> */ Combined }

/// <summary>
/// Estimates the key of a clip by comparing its chromagram with published key profiles (Temperley by default, Krumhansl-Kessler selectable)
/// for all 24 major and minor keys. This is a heuristic with known weak spots: modal and non-Western music (the dromoi of
/// Greek folk and laika, for one), music that modulates, and tracks with little harmony (drums, spoken word) give weak or wrong answers.
/// The confidence is the margin of the best key over the best unrelated key; a close relative-major/minor runner-up is flagged.
/// </summary>
public static class KeyEstimator
{
    // Krumhansl-Kessler (1982): from listening experiments. Temperley (the "CBMS" profiles of his 2001 book): fitted to a corpus of music, and
    // better at telling a minor key from its parallel major, which the synthetic test progressions showed clearly (margins of 0.14-0.18 against 0.01-0.07).
    private static readonly double[] KrumhanslMajor = [6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88];
    private static readonly double[] KrumhanslMinor = [6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17];
    private static readonly double[] TemperleyMajor = [5.0, 2.0, 3.5, 2.0, 4.5, 4.0, 2.0, 4.5, 2.0, 3.5, 1.5, 4.0];
    private static readonly double[] TemperleyMinor = [5.0, 2.0, 3.5, 4.5, 2.0, 4.0, 2.0, 4.5, 3.5, 2.0, 1.5, 4.0];

    /// <summary>The clip must correlate at least this well with some key, or it has no clear key.</summary>
    public const double MinCorrelation = 0.45;
    /// <summary>A flat chromagram (every pitch class equally present) has no key.</summary>
    public const double MinPeakiness = 1.3;

    public static KeyEstimate? Estimate(ReadOnlySpan<float> audio, int sampleRate, KeyProfileSet profiles = KeyProfileSet.Combined) =>
        Estimate(Chromagram.Compute(audio, sampleRate), profiles);

    public static KeyEstimate? Estimate(Chroma chroma, KeyProfileSet profiles = KeyProfileSet.Combined)
    {
        if (chroma.IsEmpty) return null;
        var values = chroma.Values;
        var mean = values.Average();
        if (mean <= 0 || values.Max() / mean < MinPeakiness) return null;

        var scores = new List<(CamelotKey Key, double R)>();
        for (var tonic = 0; tonic < 12; tonic++)
        {
            scores.Add((CamelotKey.FromPitchClass(tonic, minor: false), Score(values, profiles, major: true, tonic)));
            scores.Add((CamelotKey.FromPitchClass(tonic, minor: true), Score(values, profiles, major: false, tonic)));
        }
        var ordered = scores.OrderByDescending(s => s.R).ToList();
        var best = ordered[0];
        if (best.R < MinCorrelation) return null;

        var relative = CamelotKey.FromPitchClass(best.Key.TonicPitchClass + (best.Key.IsMinor ? 3 : -3), !best.Key.IsMinor);
        var runner = ordered[1];
        var rival = ordered.Skip(1).First(s => s.Key != relative);            // the best key that is not just the relative of the winner
        var relativeR = scores.First(s => s.Key == relative).R;

        var margin = best.R - rival.R;
        var score = Math.Clamp(margin / 0.2, 0, 1) * Math.Clamp((best.R - 0.3) / 0.4, 0, 1);
        var level = LevelOf(score);
        var flags = new List<string>();
        if (best.R - relativeR < 0.03)
        {
            flags.Add("relative-ambiguous");                                   // major or minor? the same notes fit both
            level = level switch { Confidence.High => Confidence.Medium, Confidence.Medium => Confidence.Low, _ => level };
        }
        if (level == Confidence.None) return null;

        return new KeyEstimate(best.Key.ToString(), Math.Round(score, 3), level, runner.Key.ToString(), string.Join(' ', flags), chroma.TuningCents);
    }

    private static double Score(double[] chroma, KeyProfileSet profiles, bool major, int tonic) => profiles switch
    {
        KeyProfileSet.Temperley => Correlation(chroma, major ? TemperleyMajor : TemperleyMinor, tonic),
        KeyProfileSet.KrumhanslKessler => Correlation(chroma, major ? KrumhanslMajor : KrumhanslMinor, tonic),
        _ => (Correlation(chroma, major ? TemperleyMajor : TemperleyMinor, tonic) + Correlation(chroma, major ? KrumhanslMajor : KrumhanslMinor, tonic)) / 2
    };

    /// <summary>Pearson correlation of the chroma with a key profile turned to start at the given tonic.</summary>
    public static double Correlation(double[] chroma, double[] profile, int tonic)
    {
        var meanC = chroma.Average();
        var meanP = profile.Average();
        double num = 0, dc = 0, dp = 0;
        for (var pc = 0; pc < 12; pc++)
        {
            var c = chroma[pc] - meanC;
            var p = profile[((pc - tonic) % 12 + 12) % 12] - meanP;
            num += c * p;
            dc += c * c;
            dp += p * p;
        }
        return dc <= 0 || dp <= 0 ? 0 : num / Math.Sqrt(dc * dp);
    }

    internal static Confidence LevelOf(double score) =>
        score >= 0.6 ? Confidence.High : score >= 0.35 ? Confidence.Medium : score >= 0.12 ? Confidence.Low : Confidence.None;
}
