namespace MusicFinder.Application.Analysis;

/// <summary>How strongly new sound starts, frame by frame: the signal the tempo is read from.</summary>
public sealed record Onsets(double[] Envelope, double FrameRate)
{
    public double Seconds => Envelope.Length / FrameRate;
}

/// <summary>
/// Spectral flux: the spectrum is cut into log-spaced bands, compressed, and the positive change of every band from one frame to
/// the next is added up. Drum hits and note attacks make peaks; sustained sound makes none. The slow average is removed so
/// loud and quiet passages weigh alike.
/// </summary>
public static class OnsetEnvelope
{
    public const int FrameSize = 1024;
    public const int Hop = 256;
    private const int Bands = 40;
    private const double LowHz = 40;
    private const double HighHz = 9000;
    private const double SmoothingSeconds = 0.4;

    public static Onsets Compute(ReadOnlySpan<float> audio, int sampleRate)
    {
        var frameRate = (double)sampleRate / Hop;
        var frames = audio.Length < FrameSize ? 0 : 1 + (audio.Length - FrameSize) / Hop;
        if (frames < 3) return new Onsets([], frameRate);

        var fft = new Fft(FrameSize);
        var window = Fft.Hann(FrameSize);
        var magnitudes = new double[FrameSize / 2 + 1];
        var edges = BandEdges(sampleRate);

        var previous = new double[Bands];
        var current = new double[Bands];
        var flux = new double[frames];
        for (var f = 0; f < frames; f++)
        {
            fft.Magnitudes(audio.Slice(f * Hop, FrameSize), window, magnitudes);
            for (var b = 0; b < Bands; b++)
            {
                double sum = 0;
                for (var bin = edges[b]; bin < edges[b + 1]; bin++) sum += magnitudes[bin];
                current[b] = Math.Log(1 + 100 * sum / FrameSize);        // compression: a quiet hat counts, a loud kick does not drown it
            }
            if (f > 0)
            {
                double total = 0;
                for (var b = 0; b < Bands; b++) total += Math.Max(0, current[b] - previous[b]);
                flux[f] = total;
            }
            (previous, current) = (current, previous);
        }

        return new Onsets(RemoveSlowAverage(flux, (int)(SmoothingSeconds * frameRate)), frameRate);
    }

    /// <summary>First FFT bin of each band; the bands are equal steps of pitch, so bass and treble matter equally.</summary>
    private static int[] BandEdges(int sampleRate)
    {
        var edges = new int[Bands + 1];
        var binHz = (double)sampleRate / FrameSize;
        var high = Math.Min(HighHz, sampleRate / 2.0 - binHz);
        var ratio = Math.Pow(high / LowHz, 1.0 / Bands);
        var last = 1;
        for (var b = 0; b <= Bands; b++)
        {
            var bin = (int)Math.Round(LowHz * Math.Pow(ratio, b) / binHz);
            last = Math.Max(last + (b == 0 ? 0 : 1), bin);              // every band holds at least one bin
            edges[b] = Math.Min(last, FrameSize / 2);
        }
        return edges;
    }

    private static double[] RemoveSlowAverage(double[] x, int window)
    {
        window = Math.Max(3, window | 1);
        var half = window / 2;
        var prefix = new double[x.Length + 1];
        for (var i = 0; i < x.Length; i++) prefix[i + 1] = prefix[i] + x[i];
        var result = new double[x.Length];
        for (var i = 0; i < x.Length; i++)
        {
            var from = Math.Max(0, i - half);
            var to = Math.Min(x.Length, i + half + 1);
            var mean = (prefix[to] - prefix[from]) / (to - from);
            result[i] = Math.Max(0, x[i] - mean);
        }
        return result;
    }
}
