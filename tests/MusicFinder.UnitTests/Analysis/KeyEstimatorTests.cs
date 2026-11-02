using MusicFinder.Application.Analysis;
using MusicFinder.Application.Similar;
using MusicFinder.Domain.Models;

namespace MusicFinder.UnitTests.Analysis;

public class KeyEstimatorTests
{
    private const int Rate = SignalFixtures.Rate;

    public static TheoryData<int, bool> AllKeys()
    {
        var data = new TheoryData<int, bool>();
        for (var pc = 0; pc < 12; pc++)
        {
            data.Add(pc, false);
            data.Add(pc, true);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(AllKeys))]
    public void AChordProgression_GivesItsKey_InAllTwentyFourKeys(int tonic, bool minor)
    {
        var e = KeyEstimator.Estimate(SignalFixtures.Chords(tonic, minor, 40), Rate);

        Assert.NotNull(e);
        Assert.Equal(CamelotKey.FromPitchClass(tonic, minor).ToString(), e!.Camelot);
        Assert.True(e.Level >= Confidence.Medium, $"{e.Camelot} level {e.Level} score {e.Score}");
    }

    [Theory]
    [InlineData(0, false, 30)]
    [InlineData(9, true, 30)]
    [InlineData(7, false, -30)]
    [InlineData(2, true, 40)]
    public void ARecordingThatIsOutOfTune_IsStillFound_AndItsTuningIsReported(int tonic, bool minor, double cents)
    {
        var e = KeyEstimator.Estimate(SignalFixtures.Chords(tonic, minor, 40, cents), Rate);

        Assert.NotNull(e);
        Assert.Equal(CamelotKey.FromPitchClass(tonic, minor).ToString(), e!.Camelot);
        Assert.InRange(e.TuningCents, cents - 8, cents + 8);
    }

    [Fact]
    public void ARecordingInTune_ReportsAboutZeroCents()
    {
        var e = KeyEstimator.Estimate(SignalFixtures.Chords(5, false, 40), Rate)!;

        Assert.InRange(e.TuningCents, -6, 6);
    }

    [Theory]
    [InlineData(KeyProfileSet.Temperley)]
    [InlineData(KeyProfileSet.KrumhanslKessler)]
    [InlineData(KeyProfileSet.Combined)]
    public void EveryProfileSet_FindsTheKeyOfAnUnambiguousMajorProgression(KeyProfileSet profiles)
    {
        var e = KeyEstimator.Estimate(SignalFixtures.Chords(2, false, 40), Rate, profiles);

        Assert.Equal(CamelotKey.FromPitchClass(2, false).ToString(), e!.Camelot);
    }

    [Fact]
    public void TemperleyTellsAMinorKeyFromItsParallelMajorMoreClearlyThanKrumhansl_OnTheTestProgressions()
    {
        var chords = SignalFixtures.Chords(9, true, 40);

        var temperley = KeyEstimator.Estimate(chords, Rate, KeyProfileSet.Temperley)!;
        var krumhansl = KeyEstimator.Estimate(chords, Rate, KeyProfileSet.KrumhanslKessler);

        Assert.Equal("8A", temperley.Camelot);
        Assert.True(temperley.Score > (krumhansl?.Score ?? 0));
    }

        [Fact]
    public void WhiteNoise_HasNoKey() => Assert.Null(KeyEstimator.Estimate(SignalFixtures.Noise(40), Rate));

    [Fact]
    public void Silence_HasNoKey() => Assert.Null(KeyEstimator.Estimate(SignalFixtures.Silence(40), Rate));

    [Fact]
    public void AClipTooShortForAnyHarmony_HasNoKey() => Assert.Null(KeyEstimator.Estimate(SignalFixtures.Chords(0, false, 0.2), Rate));

    [Fact]
    public void ADrumPatternAlone_HasNoReliableKey()
    {
        var e = KeyEstimator.Estimate(SignalFixtures.Drums(128, 40), Rate);

        Assert.True(e is null || e.Level <= Confidence.Low, e is null ? "" : $"{e.Camelot} {e.Level}");
    }

    [Fact]
    public void TheRunnerUp_IsReported_AndIsAnotherKey()
    {
        var e = KeyEstimator.Estimate(SignalFixtures.Chords(0, false, 40), Rate)!;

        Assert.NotNull(e.Runner);
        Assert.NotEqual(e.Camelot, e.Runner);
        Assert.True(CamelotKey.TryParse(e.Runner, out _));
    }

    [Fact]
    public void ATonicThatHasOnlyTheDominantAndSubdominant_StillGivesAnAnswerOrNone_NeverThrows()
    {
        var e = KeyEstimator.Estimate(new Chroma([0, 0, 0, 0, 0, 5, 0, 5, 0, 0, 0, 0], 0, 10));

        Assert.True(e is null || CamelotKey.TryParse(e.Camelot, out _));
    }

    [Fact]
    public void AFlatChroma_HasNoKey() =>
        Assert.Null(KeyEstimator.Estimate(new Chroma(Enumerable.Repeat(1.0, 12).ToArray(), 0, 100)));

    [Fact]
    public void TheResult_IsTheSameEveryTime()
    {
        var a = KeyEstimator.Estimate(SignalFixtures.Chords(3, true, 30), Rate)!;
        var b = KeyEstimator.Estimate(SignalFixtures.Chords(3, true, 30), Rate)!;

        Assert.Equal((a.Camelot, a.Score, a.Runner), (b.Camelot, b.Score, b.Runner));
    }

    [Fact]
    public void ThePitchClassesOfAKnownChroma_PickTheKnownKey()
    {
        // The major scale of C: all seven notes present, tonic strongest.
        var chroma = new double[] { 10, 0, 6, 0, 7, 6, 0, 8, 0, 6, 0, 5 };

        var e = KeyEstimator.Estimate(new Chroma(chroma, 0, 100));

        Assert.Equal("8B", e!.Camelot);
    }
}

public class CamelotKeyFormatTests
{
    [Theory]
    [InlineData(0, false, "8B", "1d", "C")]
    [InlineData(9, true, "8A", "1m", "Am")]
    [InlineData(7, false, "9B", "2d", "G")]
    [InlineData(4, true, "9A", "2m", "Em")]
    [InlineData(1, false, "3B", "8d", "Db")]
    [InlineData(1, true, "12A", "5m", "Dbm")]
    [InlineData(6, false, "2B", "7d", "F#")]
    [InlineData(6, true, "11A", "4m", "F#m")]
    [InlineData(11, false, "1B", "6d", "B")]
    [InlineData(8, true, "1A", "6m", "Abm")]
    public void APitchClass_ReadsTheSameInEveryNotation(int tonic, bool minor, string camelot, string openKey, string note)
    {
        var key = CamelotKey.FromPitchClass(tonic, minor);

        Assert.Equal(camelot, key.ToString());
        Assert.Equal(openKey, key.ToOpenKey());
        Assert.Equal(note, key.ToNoteName());
        Assert.Equal(tonic, key.TonicPitchClass);
    }

    [Fact]
    public void EveryKey_RoundTripsThroughEveryNotation_AndThroughItsPitchClass()
    {
        for (var tonic = 0; tonic < 12; tonic++)
            foreach (var minor in new[] { false, true })
            {
                var key = CamelotKey.FromPitchClass(tonic, minor);
                foreach (var notation in Enum.GetValues<KeyNotation>())
                {
                    Assert.True(CamelotKey.TryParse(key.Format(notation), out var back), $"{key} as {notation}: {key.Format(notation)}");
                    Assert.Equal(key, back);
                }
                Assert.Equal(tonic, key.TonicPitchClass);
            }
    }

    [Fact]
    public void TheTwentyFourKeys_AreAllDifferent() =>
        Assert.Equal(24, Enumerable.Range(0, 12).SelectMany(p => new[] { CamelotKey.FromPitchClass(p, false), CamelotKey.FromPitchClass(p, true) }).Distinct().Count());

    [Theory]
    [InlineData(KeyNotation.Camelot, "8A")]
    [InlineData(KeyNotation.OpenKey, "1m")]
    [InlineData(KeyNotation.NoteName, "Am")]
    public void Format_FollowsTheChosenNotation(KeyNotation notation, string expected) =>
        Assert.Equal(expected, new CamelotKey(8, true).Format(notation));
}
