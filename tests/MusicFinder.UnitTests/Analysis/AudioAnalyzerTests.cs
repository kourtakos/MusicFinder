using MusicFinder.Application.Analysis;
using MusicFinder.Application.Similar;
using MusicFinder.Domain.Models;

namespace MusicFinder.UnitTests.Analysis;

public class AudioAnalyzerTests
{
    private static DecodedAudio Decoded(float[] mono) => new(mono, SignalFixtures.Rate, mono.Length / (double)SignalFixtures.Rate, 44100, 2, "test");

    private static float[] Mix(float[] a, float[] b)
    {
        var n = Math.Max(a.Length, b.Length);
        var r = new float[n];
        for (var i = 0; i < n; i++) r[i] = (i < a.Length ? a[i] : 0) + (i < b.Length ? b[i] : 0);
        return r;
    }

    [Theory]
    [InlineData(128, 9, true)]
    [InlineData(100, 0, false)]
    [InlineData(124, 4, true)]
    public void DrumsOverChords_GiveBothTheTempoAndTheKey(double bpm, int tonic, bool minor)
    {
        var result = AudioAnalyzer.Analyze(Decoded(Mix(SignalFixtures.Drums(bpm, 60), SignalFixtures.Chords(tonic, minor, 60))));

        Assert.NotNull(result.Bpm);
        Assert.InRange(result.Bpm!.Bpm, bpm - 1.5, bpm + 1.5);
        Assert.NotNull(result.Key);
        Assert.Equal(CamelotKey.FromPitchClass(tonic, minor).ToString(), result.Key!.Camelot);
        Assert.Null(result.Message);
        Assert.InRange(result.AnalyzedSeconds, 55, 61);
    }

    [Fact]
    public void ASilentIntroAndOutro_AreIgnored()
    {
        var audio = SignalFixtures.Silence(12)
            .Concat(Mix(SignalFixtures.Drums(128, 40), SignalFixtures.Chords(2, true, 40)))
            .Concat(SignalFixtures.Silence(10)).ToArray();

        var result = AudioAnalyzer.Analyze(Decoded(audio));

        Assert.InRange(result.Bpm!.Bpm, 126.5, 129.5);
        Assert.Equal(CamelotKey.FromPitchClass(2, true).ToString(), result.Key!.Camelot);
        Assert.InRange(result.AnalyzedSeconds, 37, 42);                      // only the music counted
    }

    [Fact]
    public void Silence_GivesNoEstimateAndSaysWhy()
    {
        var result = AudioAnalyzer.Analyze(Decoded(SignalFixtures.Silence(60)));

        Assert.Null(result.Bpm);
        Assert.Null(result.Key);
        Assert.Contains("silent", result.Message);
    }

    [Fact]
    public void ATinyClip_GivesNoEstimateAndSaysWhy()
    {
        var result = AudioAnalyzer.Analyze(Decoded(SignalFixtures.Clicks(128, 3)));

        Assert.Null(result.Bpm);
        Assert.Null(result.Key);
        Assert.Contains("too little", result.Message);
    }

    [Fact]
    public void NoiseOnly_GivesNoEstimates()
    {
        var result = AudioAnalyzer.Analyze(Decoded(SignalFixtures.Noise(60)));

        Assert.Null(result.Bpm);
        Assert.Null(result.Key);
        Assert.Equal("No steady tempo and no clear key were found.", result.Message);
    }

    [Fact]
    public void RhythmWithoutHarmony_GivesATempoButNoKey()
    {
        var result = AudioAnalyzer.Analyze(Decoded(SignalFixtures.Clicks(128, 60)));

        Assert.NotNull(result.Bpm);
        Assert.True(result.Key is null || result.Key.Level <= Confidence.Low);
    }

    [Fact]
    public void HarmonyWithoutRhythm_GivesAKeyButNoReliableTempo()
    {
        var result = AudioAnalyzer.Analyze(Decoded(SignalFixtures.Chords(7, false, 60)));

        Assert.Equal(CamelotKey.FromPitchClass(7, false).ToString(), result.Key!.Camelot);
        Assert.True(result.Bpm is null || !result.Bpm.IsReliable);
    }

    [Fact]
    public void TheAnalysis_IsDeterministic()
    {
        var audio = Decoded(Mix(SignalFixtures.Drums(120, 45), SignalFixtures.Chords(5, false, 45)));

        var a = AudioAnalyzer.Analyze(audio);
        var b = AudioAnalyzer.Analyze(audio);

        Assert.Equal((a.Bpm!.Bpm, a.Bpm.Score, a.Key!.Camelot, a.Key.Score), (b.Bpm!.Bpm, b.Bpm.Score, b.Key!.Camelot, b.Key.Score));
    }

    [Fact]
    public void TheVersion_IsPositive_SoRememberedAnswersCanBeInvalidated() => Assert.True(AudioAnalyzer.Version >= 1);
}
