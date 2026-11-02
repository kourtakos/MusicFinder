using MusicFinder.Application.Analysis;
using MusicFinder.Domain.Models;

namespace MusicFinder.UnitTests.Analysis;

public class TempoEstimatorTests
{
    private const int Rate = SignalFixtures.Rate;

    private static BpmEstimate? Estimate(float[] audio) => TempoEstimator.Estimate(audio, Rate);

    /// <summary>Within one BPM of the truth, or of half or double it with that tempo offered as an alternative.</summary>
    private static void AssertTempo(BpmEstimate? e, double truth, double tolerance = 1.0)
    {
        Assert.NotNull(e);
        if (Math.Abs(e!.Bpm - truth) <= tolerance) return;
        var alternative = e.Alternatives.FirstOrDefault(a => Math.Abs(a.Bpm - truth) <= tolerance * 2);
        Assert.True(alternative is not null, $"estimated {e.Bpm}, expected {truth} (alternatives: {string.Join(", ", e.Alternatives.Select(a => a.Bpm))})");
    }

    [Theory]
    [InlineData(90)]
    [InlineData(100)]
    [InlineData(120)]
    [InlineData(128)]
    [InlineData(140)]
    [InlineData(150)]
    public void AClickTrack_IsEstimatedWithinOneBpm(double bpm)
    {
        var e = Estimate(SignalFixtures.Clicks(bpm, 60));

        Assert.NotNull(e);
        Assert.InRange(e!.Bpm, bpm - 1, bpm + 1);
        Assert.True(e.Level >= Confidence.Medium, $"level {e.Level} score {e.Score}");
        Assert.DoesNotContain("unstable", e.Flags);
    }

    [Fact]
    public void ASeventyBpmClickTrack_IsFoundAsSeventy_OrOffersItAsTheHalf()
    {
        var e = Estimate(SignalFixtures.Clicks(70, 60));

        AssertTempo(e, 70);
        Assert.Contains(e!.Alternatives, a => Math.Abs(a.Bpm - 70) < 1.5 || Math.Abs(e.Bpm - 70) < 1.5);
    }

    [Fact]
    public void ANonIntegerTempo_IsEstimatedToWithinAThirdOfABeat()
    {
        var e = Estimate(SignalFixtures.Clicks(123.5, 60));

        Assert.NotNull(e);
        Assert.InRange(e!.Bpm, 123.2, 123.8);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(96)]
    [InlineData(110)]
    public void ADrumPattern_IsEstimatedAtItsBeat_NotAtTheEighthNotes(double bpm)
    {
        var e = Estimate(SignalFixtures.Drums(bpm, 60));

        AssertTempo(e, bpm);
        Assert.InRange(e!.Bpm, bpm - 1.5, bpm + 1.5);                   // the beat, not the hats at double speed
    }

    [Fact]
    public void TheHalfAndDoubleTempo_AreAlwaysOffered_WithTheirStrength()
    {
        var e = Estimate(SignalFixtures.Drums(128, 60))!;

        Assert.Contains(e.Alternatives, a => a.Label == "double" && Math.Abs(a.Bpm - e.Bpm * 2) < 0.3);
        Assert.Contains(e.Alternatives, a => a.Label == "half" && Math.Abs(a.Bpm - e.Bpm / 2) < 0.3);
        Assert.All(e.Alternatives, a => Assert.InRange(a.RelativeScore, 0, 1.5));
    }

    [Fact]
    public void ATempoThatMoves_IsFlaggedUnstable_AndNeverConfident()
    {
        var e = Estimate(SignalFixtures.Ramp(100, 140, 90));

        Assert.NotNull(e);
        Assert.Contains("unstable", e!.Flags);
        Assert.Equal(Confidence.Low, e.Level);
        Assert.False(e.IsReliable);
    }

    [Fact]
    public void WhiteNoise_HasNoTempo() => Assert.Null(Estimate(SignalFixtures.Noise(60)));

    [Fact]
    public void Silence_HasNoTempo() => Assert.Null(Estimate(SignalFixtures.Silence(60)));

    [Fact]
    public void AClipThatIsTooShort_HasNoTempo() => Assert.Null(Estimate(SignalFixtures.Clicks(128, 5)));

    [Fact]
    public void AMediumClip_CannotBeCheckedForDrift_SoItIsNeverHighConfidence()
    {
        var e = Estimate(SignalFixtures.Clicks(128, 15));

        Assert.NotNull(e);
        Assert.InRange(e!.Bpm, 127, 129);
        Assert.True(e.Level <= Confidence.Medium);
    }

    [Fact]
    public void ASustainedChordProgression_HasNoReliableTempo()
    {
        var e = Estimate(SignalFixtures.Chords(0, false, 60));

        Assert.True(e is null || !e.IsReliable, e is null ? "" : $"{e.Bpm} {e.Level}");
    }

    [Fact]
    public void TheResult_IsTheSameEveryTime()
    {
        var a = Estimate(SignalFixtures.Drums(124, 45));
        var b = Estimate(SignalFixtures.Drums(124, 45));

        Assert.Equal(a!.Bpm, b!.Bpm);
        Assert.Equal(a.Score, b.Score);
    }

    [Fact]
    public void AnEmptyOrTinyInput_NeverThrows()
    {
        Assert.Null(Estimate([]));
        Assert.Null(Estimate(new float[100]));
    }

    [Theory]
    [InlineData(55)]
    [InlineData(170)]
    public void TheEdgesOfTheDjRange_AreStillFound(double bpm) => AssertTempo(Estimate(SignalFixtures.Clicks(bpm, 60)), bpm, 1.5);
}
