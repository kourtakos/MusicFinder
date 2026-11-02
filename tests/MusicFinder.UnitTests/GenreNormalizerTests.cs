using MusicFinder.Application.Genres;
using MusicFinder.Application.Services;
using MusicFinder.Domain.Entities;

namespace MusicFinder.UnitTests;

public class GenreNormalizerTests
{
    private static Genre G(long id, string name, long? parent = null) =>
        new() { Id = id, Name = name, NormalizedName = TextNormalizer.Normalize(name), ParentId = parent };

    private static readonly GenreNormalizer Normalizer = new(
        [G(1, "Disco"), G(2, "Nu Disco", 1), G(3, "Funk"), G(4, "Hip-Hop"), G(5, "R&B"), G(6, "Indie Rock"), G(7, "Greek"), G(8, "Laiko", 7), G(9, "Rock")],
        [
            new GenreAlias { AliasNormalized = "nudisco", GenreId = 2 },
            new GenreAlias { AliasNormalized = TextNormalizer.Normalize("λαϊκά"), GenreId = 8 },
            new GenreAlias { AliasNormalized = "indie", GenreId = 6 },
            new GenreAlias { AliasNormalized = "rnb", GenreId = 5 },
        ]);

    [Theory]
    [InlineData("Disco", new long[] { 1 })]
    [InlineData("disco", new long[] { 1 })]
    [InlineData("DISCO", new long[] { 1 })]
    [InlineData("Nu-Disco", new long[] { 2 })]          // punctuation folds to the same key as "Nu Disco"
    [InlineData("nu disco", new long[] { 2 })]
    [InlineData("NuDisco", new long[] { 2 })]            // alias
    [InlineData("Hip-Hop", new long[] { 4 })]
    [InlineData("Hip Hop", new long[] { 4 })]
    [InlineData("R&B", new long[] { 5 })]
    [InlineData("RnB", new long[] { 5 })]
    [InlineData("Indie Rock", new long[] { 6 })]
    [InlineData("Indie", new long[] { 6 })]
    [InlineData("Λαϊκά", new long[] { 8 })]
    [InlineData("ΛΑΪΚΑ", new long[] { 8 })]             // case + dialytika/tonos
    [InlineData("Disco;Funk", new long[] { 1, 3 })]
    [InlineData("Disco / Funk", new long[] { 1, 3 })]
    [InlineData("Funk, Disco", new long[] { 3, 1 })]
    [InlineData("Disco;Disco", new long[] { 1 })]        // duplicates collapse
    [InlineData("Rock (Live)", new long[] { 9 })]        // trailing qualifier is ignored on retry
    [InlineData("House [Remix]", new long[0])]
    public void Maps(string raw, long[] expected) => Assert.Equal(expected, Normalizer.Map(raw).GenreIds);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(";;;")]
    public void BlankInput_MapsToNothing(string? raw)
    {
        var result = Normalizer.Map(raw);
        Assert.Empty(result.GenreIds);
        Assert.Empty(result.Unmapped);
    }

    [Fact]
    public void UnknownTokens_AreReportedAndKnownOnesStillMap()
    {
        var result = Normalizer.Map("Disco; Vaporwave ;Funk;vaporwave");
        Assert.Equal([1, 3], result.GenreIds);
        Assert.Equal(["Vaporwave"], result.Unmapped); // reported once, original spelling kept
    }

    [Fact]
    public void NoSubstringMatching_RockabillyIsNotRock()
    {
        var result = Normalizer.Map("Rockabilly");
        Assert.Empty(result.GenreIds);
        Assert.Equal(["Rockabilly"], result.Unmapped);
    }

    [Fact]
    public void GenreNameBeatsAnAliasWithTheSameKey()
    {
        var n = new GenreNormalizer([G(1, "Disco"), G(2, "Funk")], [new GenreAlias { AliasNormalized = "disco", GenreId = 2 }]);
        Assert.Equal([1], n.Map("disco").GenreIds);
    }
}
