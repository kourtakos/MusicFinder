using MusicFinder.Application.Identification;
using MusicFinder.Domain.Entities;

namespace MusicFinder.UnitTests;

public class MusicBrainzQueryBuilderTests
{
    private static Track T(string? title = null, string? artist = null, string? album = null, string file = "x.mp3") =>
        new() { Title = title, Artist = artist, Album = album, FileName = file };

    [Fact]
    public void TagsBecomeQuotedFieldPhrases()
    {
        var q = MusicBrainzQueryBuilder.Build(MusicBrainzQueryBuilder.TermsFor(T("Le Freak", "Chic", "C'est Chic"))!);

        Assert.Equal("recording:\"Le Freak\" AND artist:\"Chic\" AND release:\"C'est Chic\"", q);
    }

    [Fact]
    public void GreekTags_AreKeptAsTheyAre()
    {
        var q = MusicBrainzQueryBuilder.Build(MusicBrainzQueryBuilder.TermsFor(T("Ξανά στη ζωή μου", "Χάρις Αλεξίου"))!);

        Assert.Equal("recording:\"Ξανά στη ζωή μου\" AND artist:\"Χάρις Αλεξίου\"", q);
    }

    [Theory]
    [InlineData("Don't Stop (Remix) [2011]: AC/DC*", "recording:\"Don't Stop (Remix) [2011]: AC/DC*\"")]   // inside quotes these mean nothing
    [InlineData("Say \"Hello\"", "recording:\"Say \\\"Hello\\\"\"")]
    [InlineData("Back\\Slash", "recording:\"Back\\\\Slash\"")]
    [InlineData("x\" OR artist:\"y", "recording:\"x\\\" OR artist:\\\"y\"")]                                // an attempt to end the phrase early
    public void SpecialCharacters_CannotChangeTheStructureOfTheQuery(string title, string expected) =>
        Assert.Equal(expected, MusicBrainzQueryBuilder.Build(MusicBrainzQueryBuilder.TermsFor(T(title))!));

    [Fact]
    public void ControlCharacters_AreDropped() =>
        Assert.Equal("recording:\"AB\"", MusicBrainzQueryBuilder.Build(MusicBrainzQueryBuilder.TermsFor(T("A\r\nB"))!));

    [Fact]
    public void WithoutATitle_TheFileNameIsSplitAsArtistDashTitle()
    {
        var terms = MusicBrainzQueryBuilder.TermsFor(T(file: "Chic - Le Freak.mp3"))!;

        Assert.Equal(("Le Freak", "Chic", true), (terms.Title, terms.Artist, terms.FromFileName));
    }

    [Theory]
    [InlineData("01 - Chic - Le Freak.mp3", "Le Freak", "Chic")]
    [InlineData("07. Chic - Le Freak.flac", "Le Freak", "Chic")]
    [InlineData("Le_Freak.mp3", "Le Freak", null)]
    public void TrackNumbersAndUnderscores_AreTidiedFromAFileName(string file, string title, string? artist)
    {
        var terms = MusicBrainzQueryBuilder.TermsFor(T(file: file))!;

        Assert.Equal((title, artist), (terms.Title, terms.Artist));
    }

    [Fact]
    public void ATaggedArtist_WinsOverOneGuessedFromTheFileName()
    {
        var terms = MusicBrainzQueryBuilder.TermsFor(T(artist: "Tagged", file: "Guessed - Song.mp3"))!;

        Assert.Equal("Tagged", terms.Artist);
    }

    [Fact]
    public void AlbumArtist_StandsInForAMissingArtist() =>
        Assert.Equal("Various", MusicBrainzQueryBuilder.TermsFor(new Track { Title = "T", AlbumArtist = "Various", FileName = "a.mp3" })!.Artist);

    [Theory]
    [InlineData(null, "")]
    [InlineData("  ", ".mp3")]
    public void NothingToSearchFor_GivesNull(string? title, string file) =>
        Assert.Null(MusicBrainzQueryBuilder.TermsFor(T(title, file: file)));
}
