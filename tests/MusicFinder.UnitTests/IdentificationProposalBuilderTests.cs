using MusicFinder.Application.Identification;
using MusicFinder.Domain.Entities;
using MusicFinder.Domain.Models;

namespace MusicFinder.UnitTests;

public class IdentificationProposalBuilderTests
{
    private const string Rec = "5f2c9a0e-7b1d-4c3a-9e84-0d6b2a1f7c33";

    private static IdentificationCandidate Candidate(string[]? genres = null) => new(
        Rec, "Ξανά στη ζωή μου", "Χάρις Αλεξίου", 225_000, IdentificationMethod.AcoustIdFingerprint, 0.97, null,
        ["duration matches"], [], genres ?? [], []);

    private static ReleaseDetails Release(string? date = "1985-03-02") => new(
        "rel-1", "Το Τραγούδι", date, "GR", "Χάρις Αλεξίου", [],
        [new ReleaseTrack("other", 1, 1, 9, "Other", "X"), new ReleaseTrack(Rec, 1, 5, 9, "Ξανά στη ζωή μου", "Χάρις Αλεξίου")]);

    private static FieldProposal Get(IReadOnlyList<FieldProposal> p, TagField f) => p.Single(x => x.Field == f);

    [Fact]
    public void EmptyFields_AreProposedAndTicked_AndTheTrackNumberComesFromTheChosenRelease()
    {
        var p = IdentificationProposalBuilder.Build(new Track(), Candidate(), Release());

        Assert.Equal("Ξανά στη ζωή μου", Get(p, TagField.Title).Proposed);
        Assert.True(Get(p, TagField.Title).Selected);
        Assert.Equal("Το Τραγούδι", Get(p, TagField.Album).Proposed);
        Assert.Equal("1985", Get(p, TagField.Year).Proposed);
        Assert.Equal("5", Get(p, TagField.TrackNumber).Proposed);
        Assert.All(p, x => Assert.True(x.Selected));
    }

    [Fact]
    public void AFieldThatAlreadyHoldsADifferentValue_IsListedButNotTicked()
    {
        var track = new Track { Title = "Old title", Artist = "Χάρις Αλεξίου" };

        var p = IdentificationProposalBuilder.Build(track, Candidate(), null);

        Assert.False(Get(p, TagField.Title).Selected);
        Assert.Equal("Old title", Get(p, TagField.Title).Current);
        Assert.DoesNotContain(p, x => x.Field == TagField.Artist);   // identical (case/space aside): nothing to change
    }

    [Fact]
    public void WithoutARelease_NoAlbumYearOrTrackNumberIsProposed()
    {
        var p = IdentificationProposalBuilder.Build(new Track(), Candidate(), null);

        Assert.DoesNotContain(p, x => x.Field is TagField.Album or TagField.AlbumArtist or TagField.Year or TagField.TrackNumber);
    }

    [Fact]
    public void BpmAndKey_AreNeverProposed()
    {
        var p = IdentificationProposalBuilder.Build(new Track(), Candidate(["Disco"]), Release());

        Assert.DoesNotContain(p, x => x.Field is TagField.Bpm or TagField.Key);
    }

    [Fact]
    public void Genres_FillAFileThatHasNone_AreTitleCased_LimitedToThree_AndCanBeTurnedOff()
    {
        var on = IdentificationProposalBuilder.Build(new Track(), Candidate(["classic rock", "art rock", "glam rock", "pop"]), null);
        var off = IdentificationProposalBuilder.Build(new Track(), Candidate(["Disco", "Funk"]), null, suggestGenres: false);

        var genre = Get(on, TagField.Genre);
        Assert.Equal("Classic Rock; Art Rock; Glam Rock", genre.Proposed);       // the three most voted, readable
        Assert.True(genre.Selected);                                           // the file has no genre: ticked like any empty field
        Assert.Equal(IdentificationProposalBuilder.FromCommunityTags, genre.Source);
        Assert.DoesNotContain(off, x => x.Field == TagField.Genre);
    }

    [Fact]
    public void AGenreTheUserAlreadyHas_IsNeverReplacedUnlessTicked()
    {
        var p = IdentificationProposalBuilder.Build(new Track { GenreRaw = "Laiko" }, Candidate(["disco"]), null);

        var genre = Get(p, TagField.Genre);
        Assert.False(genre.Selected);
        Assert.Equal(("Laiko", "Disco"), (genre.Current, genre.Proposed));
        Assert.DoesNotContain(IdentificationProposalBuilder.ToChangeSet(p).Changes.Keys, k => k == TagField.Genre);
    }

    [Fact]
    public void GenresOfTheArtist_AreLabelledAsSuch_SoTheyAreNotTakenForTheRecordings()
    {
        var candidate = Candidate(["rock"]) with { GenreLevel = "artist" };

        Assert.Equal(IdentificationProposalBuilder.FromArtistTags, Get(IdentificationProposalBuilder.Build(new Track(), candidate, null), TagField.Genre).Source);
    }

    [Fact]
    public void ASameGenreInAnotherCase_NeedsNoChange() =>
        Assert.DoesNotContain(IdentificationProposalBuilder.Build(new Track { GenreRaw = "disco" }, Candidate(["Disco"]), null), p => p.Field == TagField.Genre);

    [Fact]
    public void TheRecordingId_IsTicked_UnlessTheFileAlreadyHasIt()
    {
        var fresh = IdentificationProposalBuilder.Build(new Track(), Candidate(), null);
        var linked = IdentificationProposalBuilder.Build(new Track { MusicBrainzRecordingId = Rec.ToUpperInvariant() }, Candidate(), null);

        Assert.True(Get(fresh, TagField.MusicBrainzRecordingId).Selected);
        Assert.DoesNotContain(linked, x => x.Field == TagField.MusicBrainzRecordingId);
    }

    [Theory]
    [InlineData("1985", "1985")]
    [InlineData("1985-03", "1985")]
    [InlineData("1985-03-02", "1985")]
    [InlineData("0000", null)]
    [InlineData("2999-01-01", null)]
    [InlineData("19", null)]
    [InlineData("abcd", null)]
    [InlineData(null, null)]
    public void YearOf_AcceptsOnlyAPlausibleLeadingYear(string? date, string? expected) =>
        Assert.Equal(expected, IdentificationProposalBuilder.YearOf(date));

    [Fact]
    public void ToChangeSet_ContainsOnlyTheTickedFields()
    {
        var p = IdentificationProposalBuilder.Build(new Track { Title = "Old" }, Candidate(), Release());
        var untickedTitle = p.Single(x => x.Field == TagField.Title);
        Assert.False(untickedTitle.Selected);

        var set = IdentificationProposalBuilder.ToChangeSet(p);

        Assert.DoesNotContain(TagField.Title, set.Changes.Keys);
        Assert.Equal("1985", set.Changes[TagField.Year]);
        Assert.Equal(Rec, set.Changes[TagField.MusicBrainzRecordingId]);
    }

    [Fact]
    public void TheScoreText_NamesWhereTheScoreCameFrom()
    {
        var fp = Candidate();
        var search = fp with { AcoustIdScore = null, MusicBrainzSearchScore = 87, Method = IdentificationMethod.MusicBrainzTagSearch };
        var id = fp with { AcoustIdScore = null, Method = IdentificationMethod.ExistingRecordingId };

        Assert.Equal("AcoustID score 0.97", fp.ScoreText);
        Assert.Equal("MusicBrainz search score 87 of 100", search.ScoreText);
        Assert.Equal("From the file's own MusicBrainz id", id.ScoreText);
    }

    [Theory]
    [InlineData(true, true, true, true, true, true)]
    [InlineData(false, true, true, true, true, false)]   // switched off
    [InlineData(true, false, true, true, true, false)]   // never saw what is sent
    [InlineData(true, true, false, true, true, true)]    // tag search needs only the contact (printed below)
    public void TagSearchNeedsOnlyTheContact_FingerprintingNeedsMore(bool enabled, bool consented, bool key, bool contact, bool fpcalc, bool tagSearch)
    {
        var r = new IdentificationReadiness(enabled, consented, key, contact, fpcalc ? new FingerprinterInfo("fpcalc.exe", "settings") : null);

        Assert.Equal(tagSearch, r.CanTagSearch);
        Assert.Equal(tagSearch && key && fpcalc, r.CanFingerprint);
    }
}
