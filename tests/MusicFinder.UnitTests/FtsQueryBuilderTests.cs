using MusicFinder.Application.Search;
using MusicFinder.Infrastructure.Search;

namespace MusicFinder.UnitTests;

public class FtsQueryBuilderTests
{
    private static string? Build(string input) => FtsQueryBuilder.Build(SearchQueryParser.Parse(input).Terms);

    [Fact]
    public void FreeToken_BecomesPrefixOfRawOrNormalized() =>
        Assert.Equal("(\"disco\"* OR NormalizedText:\"disco\"*)", Build("disco"));

    [Fact]
    public void Greek_AddsAccentFreeVariant() =>
        Assert.Equal("(\"Αλεξίου\"* OR NormalizedText:\"αλεξιου\"*)", Build("Αλεξίου"));

    [Fact]
    public void Tokens_AreAndJoined() =>
        Assert.Equal(
            "(\"disco\"* OR NormalizedText:\"disco\"*) AND (\"funk\"* OR NormalizedText:\"funk\"*)",
            Build("disco funk"));

    [Fact]
    public void Phrase_HasNoPrefixStar() =>
        Assert.Equal("(\"le freak\" OR NormalizedText:\"le freak\")", Build("\"le freak\""));

    [Fact]
    public void FieldScoped_UsesColumnFilter() =>
        Assert.Equal("Artist:\"chic\"*", Build("artist:chic"));

    [Theory]
    [InlineData("AND")]
    [InlineData("OR")]
    [InlineData("NOT")]
    [InlineData("NEAR(a b)")]
    [InlineData("col:value")]
    [InlineData("a*b")]
    [InlineData("-minus")]
    [InlineData("(paren")]
    [InlineData("it's")]
    public void FtsSyntax_IsAlwaysQuoted(string input)
    {
        var result = Build(input)!;
        // Every user-supplied value must sit inside a double-quoted string.
        Assert.DoesNotContain(" AND AND", result);
        Assert.StartsWith("(\"", result);
    }

    [Fact]
    public void EmbeddedQuotes_AreDoubled() =>
        Assert.Contains("\"say \"\"hi\"\"\"", FtsQueryBuilder.Build([new TextTerm(null, "say \"hi\"", true)]));

    [Theory]
    [InlineData("!!!")]
    [InlineData("- *")]
    [InlineData("")]
    public void PunctuationOnly_YieldsNoMatchExpression(string input) => Assert.Null(Build(input));
}
