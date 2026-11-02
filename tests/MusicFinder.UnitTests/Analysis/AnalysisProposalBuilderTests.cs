using MusicFinder.Application.Analysis;
using MusicFinder.Domain.Entities;
using MusicFinder.Domain.Models;

namespace MusicFinder.UnitTests.Analysis;

public class AnalysisProposalBuilderTests
{
    private static TrackAnalysis Analysis(double? bpm = 123.5, Confidence bpmLevel = Confidence.High, string bpmFlags = "",
        string? key = "8A", Confidence keyLevel = Confidence.High, string keyFlags = "", double tuning = 0, string? runner = "8B") => new(
        1, 1,
        bpm is null ? null : new BpmEstimate(bpm.Value, 0.8, bpmLevel, [], bpmFlags),
        key is null ? null : new KeyEstimate(key, 0.7, keyLevel, runner, keyFlags, tuning),
        90, "test", null, DateTime.UtcNow);

    private static Track T(double? bpm = null, string? key = null) => new() { Bpm = bpm, MusicalKey = key };

    private static AnalysisProposals Build(Track t, TrackAnalysis a, KeyNotation n = KeyNotation.Camelot, bool overwrite = false, TempoChoice tempo = TempoChoice.Estimate) =>
        AnalysisProposalBuilder.Build(t, a, n, overwrite, tempo);

    // ===== BPM =====

    [Fact]
    public void ABpmForAnEmptyTag_IsProposedAsAWholeNumber_AndTickedWhenConfident()
    {
        var p = Build(T(), Analysis(bpm: 123.5)).Bpm!;

        Assert.Equal(("124", null), (p.Proposed, p.Current));
        Assert.True(p.Selected);
        Assert.True(p.CanApply);
        Assert.Contains("123.5 BPM, written as the whole number 124", p.Note);
    }

    [Fact]
    public void ANearlyWholeBpm_NeedsNoRoundingNote() =>
        Assert.Null(Build(T(), Analysis(bpm: 128.02)).Bpm!.Note);

    [Fact]
    public void ABpmAlreadyInTheFile_IsNotReplaced_UnlessAllowed_AndNeverTicked()
    {
        var blocked = Build(T(bpm: 100), Analysis(bpm: 128)).Bpm!;
        var allowed = Build(T(bpm: 100), Analysis(bpm: 128), overwrite: true).Bpm!;

        Assert.Equal(("100", false, false), (blocked.Current, blocked.CanApply, blocked.Selected));
        Assert.True(allowed.CanApply);
        Assert.True(allowed.Selected);                          // the user switched overwriting on, so a reliable estimate is ticked
    }

    [Fact]
    public void WhenTheFileAlreadyAgrees_NothingIsProposed()
    {
        Assert.Null(Build(T(bpm: 128), Analysis(bpm: 127.8)).Bpm);               // 128 rounded
        Assert.Null(Build(T(bpm: 128.2), Analysis(bpm: 128)).Bpm);
    }

    [Theory]
    [InlineData(Confidence.Low)]
    [InlineData(Confidence.Medium)]
    public void ATempoThatIsNotConfident_IsOfferedButNeverTickedForTheUser(Confidence level)
    {
        var p = Build(T(), Analysis(bpmLevel: level)).Bpm!;

        Assert.True(p.CanApply);
        Assert.False(p.Selected);
    }

    [Fact]
    public void ATempoThatDrifts_IsNeverTicked_AndSaysWhy()
    {
        var p = Build(T(), Analysis(bpmLevel: Confidence.Low, bpmFlags: "unstable")).Bpm!;

        Assert.False(p.Selected);
        Assert.Contains("drifts", p.Note);
    }

    [Fact]
    public void AnAmbiguousOctave_IsMentioned()
    {
        var p = Build(T(), Analysis(bpm: 90, bpmLevel: Confidence.Medium, bpmFlags: "octave-ambiguous")).Bpm!;

        Assert.Contains("half or double", p.Note);
    }

    [Theory]
    [InlineData(TempoChoice.Half, 64, "64")]
    [InlineData(TempoChoice.Double, 64, "128")]
    [InlineData(TempoChoice.Estimate, 64, "64")]
    public void HalfOrDoubleCanBeChosen(TempoChoice choice, double estimate, string expected)
    {
        var p = Build(T(), Analysis(bpm: choice == TempoChoice.Half ? estimate * 2 : estimate), tempo: choice).Bpm!;

        Assert.Equal(expected, p.Proposed);
        if (choice != TempoChoice.Estimate) Assert.Contains("you chose", p.Note);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(400)]
    public void ATempoTheTagCannotHold_IsNotProposed(double bpm) => Assert.Null(Build(T(), Analysis(bpm: bpm)).Bpm);

    [Fact]
    public void NoBpmEstimate_MeansNoBpmProposal() => Assert.Null(Build(T(), Analysis(bpm: null)).Bpm);

    // ===== key =====

    [Theory]
    [InlineData(KeyNotation.Camelot, "8A")]
    [InlineData(KeyNotation.OpenKey, "1m")]
    [InlineData(KeyNotation.NoteName, "Am")]
    public void TheKey_IsWrittenInTheChosenNotation(KeyNotation notation, string expected)
    {
        var p = Build(T(), Analysis(), notation).Key!;

        Assert.Equal(expected, p.Proposed);
        Assert.True(p.CanApply);
    }

    [Theory]
    [InlineData(Confidence.High)]
    [InlineData(Confidence.Medium)]
    [InlineData(Confidence.Low)]
    public void AKey_IsNeverTickedForTheUser_HoweverConfident_AndSaysItIsLessReliable(Confidence level)
    {
        var p = Build(T(), Analysis(keyLevel: level)).Key!;

        Assert.False(p.Selected);
        Assert.True(p.CanApply);
        Assert.Contains("less reliable than tempos", p.Note);
    }

    [Fact]
    public void AKeyAlreadyInTheFile_InAnyNotation_IsNotProposedAgain()
    {
        Assert.Null(Build(T(key: "8A"), Analysis()).Key);
        Assert.Null(Build(T(key: "Am"), Analysis()).Key);
        Assert.Null(Build(T(key: "1m"), Analysis(), KeyNotation.NoteName).Key);
    }

    [Fact]
    public void ADifferentKeyInTheFile_IsNotReplaced_UnlessAllowed()
    {
        var blocked = Build(T(key: "5B"), Analysis()).Key!;
        var allowed = Build(T(key: "5B"), Analysis(), overwrite: true).Key!;

        Assert.Equal(("5B", false, false), (blocked.Current, blocked.CanApply, blocked.Selected));
        Assert.True(allowed.CanApply);
    }

    [Fact]
    public void ABlankKeyTag_CountsAsEmpty() => Assert.True(Build(T(key: "   "), Analysis()).Key!.CanApply);

    [Fact]
    public void ARelativeKeyThatFitsAlmostAsWell_IsMentioned_AndSoIsAnOffPitchRecording()
    {
        var p = Build(T(), Analysis(keyLevel: Confidence.Medium, keyFlags: "relative-ambiguous", tuning: -32)).Key!;

        Assert.Contains("relative major or minor", p.Note);
        Assert.Contains("-32 cents", p.Note);
        Assert.Contains("next best key is 8B", p.Note);
    }

    [Fact]
    public void NoKeyEstimate_MeansNoKeyProposal() => Assert.Null(Build(T(), Analysis(key: null)).Key);

    // ===== the change set =====

    [Fact]
    public void TheChangeSet_HoldsOnlyTickedAndApplicableFields()
    {
        var p = Build(T(bpm: 100), Analysis(), overwrite: false);          // the BPM is blocked; the key is open but not ticked

        Assert.Empty(AnalysisProposalBuilder.ToChangeSet(p.All).Changes);

        var set = AnalysisProposalBuilder.ToChangeSet(new[] { p.Key! with { Selected = true }, p.Bpm! with { Selected = true } });   // ticking the blocked BPM changes nothing
        Assert.Equal([TagField.Key], set.Changes.Keys);
        Assert.Equal("8A", set.Changes[TagField.Key]);
    }

    [Fact]
    public void TheProposedBpmText_IsWhatTheTagWriterWillStore()
    {
        var p = Build(T(), Analysis(bpm: 123.5)).Bpm!;

        Assert.Equal(p.Proposed, MusicFinder.Application.Tags.TagChangeValidator.Canonicalize(TagField.Bpm, p.Proposed, out _));
        Assert.Equal(p.Proposed, MusicFinder.Application.Tags.TagChangeValidator.Canonicalize(TagField.Bpm, "123.5", out _));
    }

    [Fact]
    public void TheProposedKeyText_PassesTheTagValidation()
    {
        foreach (var notation in Enum.GetValues<KeyNotation>())
            for (var n = 1; n <= 12; n++)
                foreach (var letter in new[] { "A", "B" })
                {
                    var p = Build(T(), Analysis(key: n + letter), notation).Key!;
                    Assert.Empty(MusicFinder.Application.Tags.TagChangeValidator.Validate(AnalysisProposalBuilder.ToChangeSet(new[] { p with { Selected = true } })));
                }
    }
}
