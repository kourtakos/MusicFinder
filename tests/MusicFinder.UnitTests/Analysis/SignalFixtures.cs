namespace MusicFinder.UnitTests.Analysis;

/// <summary>Synthetic audio with a known answer: the only way to test tempo and key estimation without anyone's music files.</summary>
internal static class SignalFixtures
{
    public const int Rate = 22050;

    public static float[] Silence(double seconds, int rate = Rate) => new float[(int)(seconds * rate)];

    public static float[] Noise(double seconds, int seed = 1, double amplitude = 0.3, int rate = Rate)
    {
        var rng = new Random(seed);
        var a = new float[(int)(seconds * rate)];
        for (var i = 0; i < a.Length; i++) a[i] = (float)((rng.NextDouble() * 2 - 1) * amplitude);
        return a;
    }

    /// <summary>Short decaying noise bursts exactly one beat apart.</summary>
    public static float[] Clicks(double bpm, double seconds, int rate = Rate)
    {
        var a = new float[(int)(seconds * rate)];
        var rng = new Random(7);
        var period = 60.0 / bpm;
        for (var t = 0.0; t < seconds; t += period) Burst(a, t, 0.025, 0.9, rng, rate);
        return a;
    }

    /// <summary>A four-on-the-floor beat: kick on every beat, snare on 2 and 4, quiet hats on the eighths.</summary>
    public static float[] Drums(double bpm, double seconds, int rate = Rate)
    {
        var a = new float[(int)(seconds * rate)];
        var rng = new Random(11);
        var beat = 60.0 / bpm;
        for (var i = 0; i * beat < seconds; i++)
        {
            Kick(a, i * beat, rate);
            if (i % 2 == 1) Burst(a, i * beat, 0.09, 0.45, rng, rate);                  // snare
            Burst(a, i * beat + beat / 2, 0.015, 0.15, rng, rate);                       // off-beat hat
        }
        return a;
    }

    /// <summary>A tempo that moves steadily from one BPM to another (no fixed grid).</summary>
    public static float[] Ramp(double fromBpm, double toBpm, double seconds, int rate = Rate)
    {
        var a = new float[(int)(seconds * rate)];
        var rng = new Random(5);
        var t = 0.0;
        while (t < seconds)
        {
            Burst(a, t, 0.025, 0.9, rng, rate);
            var bpm = fromBpm + (toBpm - fromBpm) * (t / seconds);
            t += 60.0 / bpm;
        }
        return a;
    }

    /// <summary>
    /// A chord progression (I-IV-V-I, or i-iv-V-i) with a bass note and harmonics, about two seconds a chord, unevenly, in the key whose tonic is the
    /// given pitch class (0 = C). <paramref name="detuneCents"/> shifts everything off concert pitch.
    /// </summary>
    public static float[] Chords(int tonicPitchClass, bool minor, double seconds, double detuneCents = 0, int rate = Rate)
    {
        var a = new float[(int)(seconds * rate)];
        int[] degrees = [0, 5, 7, 0];
        var thirds = minor ? 3 : 4;
        var tune = Math.Pow(2, detuneCents / 1200);
        // Chords of uneven length, so the changes do not form a steady pulse that a tempo estimator could mistake for a beat.
        double[] lengths = [2.3, 1.7, 2.9, 2.1];
        var at = 0.0;
        for (var c = 0; at < seconds; c++)
        {
            var chordStart = at;
            var chordLength = lengths[c % lengths.Length];
            at += chordLength;
            var degree = degrees[c % degrees.Length];
            var root = tonicPitchClass + degree;
            // The fifth degree is major in both modes (the dominant): that is what makes a minor key sound minor and resolve.
            var third = degree == 7 ? 4 : thirds;
            int[] notes = [root, root + third, root + 7];
            var start = (int)(chordStart * rate);
            var end = Math.Min(a.Length, (int)((chordStart + chordLength) * rate));
            foreach (var note in notes) AddTone(a, start, end, 60 + note, 0.18, tune, rate);        // chord tones around C4
            AddTone(a, start, end, 36 + root, 0.28, tune, rate);                                       // bass around C2/C3
        }
        return a;
    }

    private static void AddTone(float[] a, int start, int end, int midi, double amplitude, double tune, int rate)
    {
        var f0 = 440.0 * Math.Pow(2, (midi - 69) / 12.0) * tune;
        for (var h = 1; h <= 5; h++)
        {
            var f = f0 * h;
            if (f > rate / 2.2) break;
            var gain = amplitude / h;
            for (var i = start; i < end; i++)
            {
                var t = (double)(i - start) / rate;
                var env = Math.Min(1.0, t / 0.02) * Math.Min(1.0, (end - i) / (0.03 * rate));   // a short attack and release, no clicks
                a[i] += (float)(gain * env * Math.Sin(2 * Math.PI * f * t));
            }
        }
    }

    private static void Kick(float[] a, double at, int rate)
    {
        var start = (int)(at * rate);
        var length = (int)(0.12 * rate);
        for (var i = 0; i < length && start + i < a.Length; i++)
        {
            var t = (double)i / rate;
            var freq = 50 + 90 * Math.Exp(-t * 40);
            a[start + i] += (float)(0.9 * Math.Exp(-t * 28) * Math.Sin(2 * Math.PI * freq * t));
        }
    }

    private static void Burst(float[] a, double at, double length, double amplitude, Random rng, int rate)
    {
        var start = (int)(at * rate);
        var n = (int)(length * rate);
        for (var i = 0; i < n && start + i < a.Length; i++)
            a[start + i] += (float)(amplitude * Math.Exp(-5.0 * i / n) * (rng.NextDouble() * 2 - 1));
    }
}
