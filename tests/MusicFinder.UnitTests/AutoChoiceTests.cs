using MusicFinder.Application.Identification;
using MusicFinder.Domain.Entities;
using MusicFinder.Domain.Models;

namespace MusicFinder.UnitTests;

public class AutoChoiceTests
{
    private static Track T(string? title = "Ξανά στη ζωή μου", string? artist = "Χάρις Αλεξίου", string? album = null, long? ms = 225_000) =>
        new() { Title = title, Artist = artist, Album = album, DurationMs = ms, FileName = "a.mp3" };

    private static IdentificationCandidate Fp(double score, string title = "Ξανά στη ζωή μου", string artist = "Χάρις Αλεξίου", long? ms = 225_000, params ReleaseSummary[] releases) =>
        new("id-" + score, title, artist, ms, IdentificationMethod.AcoustIdFingerprint, score, null, [], releases, [], []);

    private static IdentificationCandidate Search(int score, string title = "Ξανά στη ζωή μου", string artist = "Χάρις Αλεξίου", long? ms = 225_000) =>
        new("id-s" + score, title, artist, ms, IdentificationMethod.MusicBrainzTagSearch, null, score, [], [], [], []);

    private static ReleaseSummary R(string id, string title, string? date = "1985", string? status = "Official", string? primary = "Album", params string[] secondary) =>
        new(id, title, date, "GR", status, "rg-" + id, primary, secondary);

    // ===== AcoustID =====

    [Fact]
    public void AClearAudioMatch_IsConfident() =>
        Assert.True(AutoChoice.Decide(T(), [Fp(0.97), Fp(0.55)]).Confident);

    [Fact]
    public void ALowAudioScore_IsLeftForTheUser()
    {
        var d = AutoChoice.Decide(T(), [Fp(0.80)]);

        Assert.False(d.Confident);
        Assert.Contains("0.80", d.Reason);
    }

    [Fact]
    public void TwoAlmostEqualAudioMatches_AreTooCloseToCall()
    {
        var d = AutoChoice.Decide(T(), [Fp(0.95), Fp(0.91)]);

        Assert.False(d.Confident);
        Assert.Contains("almost equally", d.Reason);
        Assert.NotNull(d.Candidate);                                        // the best is still the one shown first
    }

    [Fact]
    public void TheAudioMatch_DoesNotNeedTheTagsToAgree()
    {
        // A file with wrong or missing tags is exactly what fingerprinting is for.
        Assert.True(AutoChoice.Decide(T(title: null, artist: null), [Fp(0.97)]).Confident);
    }

    // ===== tag search =====

    [Fact]
    public void ASearchHit_IsConfident_OnlyWhenTitleArtistAndLengthAgree() =>
        Assert.True(AutoChoice.Decide(T(), [Search(100)]).Confident);

    [Theory]
    [InlineData(89, false)]
    [InlineData(90, true)]
    public void TheSearchScoreThreshold_IsInclusive(int score, bool confident) =>
        Assert.Equal(confident, AutoChoice.Decide(T(), [Search(score)]).Confident);

    [Fact]
    public void TitleAndArtist_AreComparedIgnoringCaseAccentsAndFinalSigma() =>
        Assert.True(AutoChoice.Decide(T("ΞΑΝΑ ΣΤΗ ΖΩΗ ΜΟΥ", "χαρις αλεξιου"), [Search(100)]).Confident);

    [Theory]
    [InlineData("Another Song", "Χάρις Αλεξίου", "title differs")]
    [InlineData("Ξανά στη ζωή μου", "Someone Else", "artist differs")]
    public void ADifferentTitleOrArtist_IsLeftForTheUser(string title, string artist, string reason)
    {
        var d = AutoChoice.Decide(T(), [Search(100, title, artist)]);

        Assert.False(d.Confident);
        Assert.Contains(reason, d.Reason);
    }

    [Fact]
    public void ADifferentLength_IsLeftForTheUser_AndSoIsAMissingOne()
    {
        Assert.Contains("length differs", AutoChoice.Decide(T(), [Search(100, ms: 300_000)]).Reason);
        Assert.Contains("no length", AutoChoice.Decide(T(), [Search(100, ms: null)]).Reason);
        Assert.True(AutoChoice.Decide(T(ms: null), [Search(100, ms: null)]).Confident);   // nothing to compare on either side
    }

    [Fact]
    public void AFileWithoutATitle_NeverGetsAConfidentSearchMatch()
    {
        var d = AutoChoice.Decide(T(title: null), [Search(100)]);

        Assert.False(d.Confident);
        Assert.Contains("file name", d.Reason);
    }

    [Fact]
    public void ATrackWithoutAnArtistTag_IsJudgedOnTitleAndLength() =>
        Assert.True(AutoChoice.Decide(T(artist: null), [Search(100)]).Confident);

    [Fact]
    public void TheFilesOwnId_IsAlwaysConfident()
    {
        var c = new IdentificationCandidate("id", "Whatever", "Anyone", null, IdentificationMethod.ExistingRecordingId, null, null, [], [], [], []);

        Assert.True(AutoChoice.Decide(T(), [c]).Confident);
    }

    [Fact]
    public void NoCandidates_IsNotConfident()
    {
        var d = AutoChoice.Decide(T(), []);

        Assert.False(d.Confident);
        Assert.Null(d.Candidate);
    }

    // ===== releases =====

    [Fact]
    public void Releases_AreOrdered_OfficialAlbumsFirst_PlainBeforeCompilations_EarliestFirst()
    {
        var ordered = AutoChoice.OrderReleases([
            R("boot", "Boot", "1984", "Bootleg"),
            R("comp", "Greatest Hits", "1999", "Official", "Album", "Compilation"),
            R("reissue", "Reissue", "2005"),
            R("orig", "Original", "1985"),
            R("single", "Single", "1985", "Official", "Single")]);

        Assert.Equal(["orig", "reissue", "comp", "single", "boot"], ordered.Select(r => r.ReleaseId));
    }

    [Fact]
    public void TheAlbumAlreadyInTheFile_WinsWhenOneReleaseHasThatTitle()
    {
        var c = Fp(0.97, releases: [R("orig", "Το Τραγούδι"), R("comp", "Greatest Hits", "1999", "Official", "Album", "Compilation")]);

        Assert.Equal("comp", AutoChoice.PickRelease(T(album: "greatest hits"), c)!.ReleaseId);
        Assert.Equal("orig", AutoChoice.PickRelease(T(album: "No such album"), c)!.ReleaseId);   // falls back to the order
        Assert.Equal("orig", AutoChoice.PickRelease(T(), c)!.ReleaseId);
    }

    [Fact]
    public void ACandidateWithNoReleases_HasNoRelease() =>
        Assert.Null(AutoChoice.PickRelease(T(), Fp(0.97)));
}
