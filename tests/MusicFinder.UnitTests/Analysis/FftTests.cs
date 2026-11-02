using MusicFinder.Application.Analysis;

namespace MusicFinder.UnitTests.Analysis;

public class FftTests
{
    private static float[] Sine(double hz, int n, int rate = 22050) =>
        Enumerable.Range(0, n).Select(i => (float)Math.Sin(2 * Math.PI * hz * i / rate)).ToArray();

    [Theory]
    [InlineData(440.0)]
    [InlineData(1000.0)]
    [InlineData(55.0)]
    public void ASine_PeaksInTheBinOfItsFrequency(double hz)
    {
        const int size = 4096;
        var fft = new Fft(size);
        var mags = new double[size / 2 + 1];

        fft.Magnitudes(Sine(hz, size), Fft.Hann(size), mags);

        var peak = Array.IndexOf(mags, mags.Max());
        Assert.InRange(peak * 22050.0 / size, hz - 22050.0 / size, hz + 22050.0 / size);       // within one bin
    }

    [Fact]
    public void TwoSines_GiveTwoSeparatePeaks()
    {
        const int size = 4096;
        var mix = Sine(300, size).Zip(Sine(1200, size), (x, y) => x + y).ToArray();
        var mags = new double[size / 2 + 1];

        new Fft(size).Magnitudes(mix, Fft.Hann(size), mags);

        var top = mags.Select((m, i) => (m, i)).OrderByDescending(p => p.m).Take(6).Select(p => p.i * 22050.0 / size).ToList();
        Assert.Contains(top, f => Math.Abs(f - 300) < 12);
        Assert.Contains(top, f => Math.Abs(f - 1200) < 12);
    }

    [Fact]
    public void ADcOffset_LandsInTheFirstBin_AndSilenceIsZero()
    {
        const int size = 1024;
        var fft = new Fft(size);
        var mags = new double[size / 2 + 1];

        fft.Magnitudes(Enumerable.Repeat(0.5f, size).ToArray(), Fft.Hann(size), mags);
        Assert.True(mags[0] > 100);
        Assert.True(mags[0] > 20 * mags[10]);

        fft.Magnitudes(new float[size], Fft.Hann(size), mags);
        Assert.All(mags, m => Assert.Equal(0, m, 12));
    }

    [Fact]
    public void AShortFrame_IsTreatedAsPaddedWithSilence_NotAnError()
    {
        const int size = 1024;
        var mags = new double[size / 2 + 1];

        new Fft(size).Magnitudes(Sine(440, 300), Fft.Hann(size), mags);

        Assert.True(mags.Max() > 0);
    }

    [Fact]
    public void AnInstance_CanBeReused_WithTheSameResults()
    {
        const int size = 2048;
        var fft = new Fft(size);
        var a = new double[size / 2 + 1];
        var b = new double[size / 2 + 1];
        var window = Fft.Hann(size);
        var signal = Sine(700, size);

        fft.Magnitudes(signal, window, a);
        fft.Magnitudes(Sine(90, size), window, b);              // something else in between
        fft.Magnitudes(signal, window, b);

        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1000)]
    public void ASizeThatIsNotAPowerOfTwo_IsRefused(int size) => Assert.Throws<ArgumentException>(() => new Fft(size));

    [Fact]
    public void TheHannWindow_IsZeroAtTheEdgesAndOneInTheMiddle()
    {
        var w = Fft.Hann(1024);

        Assert.Equal(0, w[0], 12);
        Assert.Equal(1, w[512], 12);
        Assert.All(w, x => Assert.InRange(x, 0, 1));
    }
}

public class SilenceGateTests
{
    [Fact]
    public void Silence_HasNoActiveSeconds() =>
        Assert.Equal(0, SilenceGate.Measure(SignalFixtures.Silence(10), SignalFixtures.Rate).ActiveSeconds);

    [Fact]
    public void AVeryQuietHiss_BelowMinus60dB_CountsAsSilence() =>
        Assert.Equal(0, SilenceGate.Measure(SignalFixtures.Noise(5, amplitude: 0.0005), SignalFixtures.Rate).ActiveSeconds);

    [Fact]
    public void Music_IsActive_AndASilentIntroAndOutroAreTrimmed()
    {
        var all = SignalFixtures.Silence(3).Concat(SignalFixtures.Chords(0, false, 8)).Concat(SignalFixtures.Silence(2)).ToArray();

        var span = SilenceGate.Measure(all, SignalFixtures.Rate);

        Assert.InRange(span.ActiveSeconds, 7.5, 8.1);
        Assert.InRange(span.StartSample / (double)SignalFixtures.Rate, 2.9, 3.1);
        Assert.InRange(span.EndSample / (double)SignalFixtures.Rate, 10.9, 11.1);
        Assert.Equal(span.EndSample - span.StartSample, span.Length);
    }

    [Fact]
    public void AnEmptyClip_IsHandled() =>
        Assert.Equal(0, SilenceGate.Measure(ReadOnlySpan<float>.Empty, SignalFixtures.Rate).Length);
}
