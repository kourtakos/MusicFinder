namespace MusicFinder.Application.Analysis;

/// <summary>
/// A radix-2 fast Fourier transform on real audio frames, written here so the analysis needs no DSP package.
/// One instance per frame size; it keeps its tables and is not thread-safe (use one per worker).
/// </summary>
public sealed class Fft
{
    private readonly int _n;
    private readonly double[] _cos;
    private readonly double[] _sin;
    private readonly int[] _reverse;
    private readonly double[] _re;
    private readonly double[] _im;

    public int Size => _n;

    public Fft(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0) throw new ArgumentException("The FFT size must be a power of two.", nameof(size));
        _n = size;
        _re = new double[size];
        _im = new double[size];
        _cos = new double[size / 2];
        _sin = new double[size / 2];
        for (var i = 0; i < size / 2; i++)
        {
            _cos[i] = Math.Cos(2 * Math.PI * i / size);
            _sin[i] = -Math.Sin(2 * Math.PI * i / size);
        }
        _reverse = new int[size];
        var bits = (int)Math.Log2(size);
        for (var i = 0; i < size; i++)
        {
            var r = 0;
            for (var b = 0; b < bits; b++) if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
            _reverse[i] = r;
        }
    }

    /// <summary>
    /// Magnitudes of the first Size/2 + 1 bins of the windowed frame. Samples missing at the end of the frame count as silence.
    /// <paramref name="window"/> must have <see cref="Size"/> entries.
    /// </summary>
    public void Magnitudes(ReadOnlySpan<float> frame, ReadOnlySpan<double> window, Span<double> magnitudes)
    {
        for (var i = 0; i < _n; i++)
        {
            var sample = i < frame.Length ? frame[i] * window[i] : 0.0;
            var target = _reverse[i];
            _re[target] = sample;
            _im[target] = 0;
        }

        for (var length = 2; length <= _n; length <<= 1)
        {
            var half = length / 2;
            var step = _n / length;
            for (var start = 0; start < _n; start += length)
            {
                for (var k = 0; k < half; k++)
                {
                    var c = _cos[k * step];
                    var s = _sin[k * step];
                    var a = start + k;
                    var b = a + half;
                    var tr = _re[b] * c - _im[b] * s;
                    var ti = _re[b] * s + _im[b] * c;
                    _re[b] = _re[a] - tr;
                    _im[b] = _im[a] - ti;
                    _re[a] += tr;
                    _im[a] += ti;
                }
            }
        }

        var bins = Math.Min(magnitudes.Length, _n / 2 + 1);
        for (var i = 0; i < bins; i++) magnitudes[i] = Math.Sqrt(_re[i] * _re[i] + _im[i] * _im[i]);
    }

    /// <summary>A Hann window: smooth edges, so a frame cut from the middle of a note does not smear across the spectrum.</summary>
    public static double[] Hann(int size)
    {
        var w = new double[size];
        for (var i = 0; i < size; i++) w[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / size);
        return w;
    }
}
