using MusicFinder.Application.Search;

namespace MusicFinder.UnitTests;

public class SearchQueryParserTests
{
    [Fact]
    public void FreeTokens_AndQuotedPhrases()
    {
        var q = SearchQueryParser.Parse("disco \"le freak\" funk");
        Assert.Equal(
            [new TextTerm(null, "disco", false), new TextTerm(null, "le freak", true), new TextTerm(null, "funk", false)],
            q.Terms);
        Assert.Empty(q.Warnings);
    }

    [Fact]
    public void FieldFilters_AreParsed()
    {
        var q = SearchQueryParser.Parse("artist:madonna title:\"like a prayer\" ext:FLAC key:8a bpm:115-125 year:1980-1989");
        Assert.Contains(new TextTerm("artist", "madonna", false), q.Terms);
        Assert.Contains(new TextTerm("title", "like a prayer", true), q.Terms);
        Assert.Equal(".flac", q.Extension);
        Assert.Equal("8A", q.MusicalKey);
        Assert.Equal((115d, 125d), (q.BpmMin, q.BpmMax));
        Assert.Equal((1980, 1989), (q.YearMin, q.YearMax));
    }

    [Theory]
    [InlineData("decade:80s", 1980)]
    [InlineData("decade:1990s", 1990)]
    [InlineData("decade:10s", 2010)]
    [InlineData("decade:00s", 2000)]
    public void Decade_MapsToYearRange(string input, int start)
    {
        var q = SearchQueryParser.Parse(input);
        Assert.Equal((start, start + 9), (q.YearMin, q.YearMax));
    }

    [Fact]
    public void SingleBpm_IsExactRange_AndReversedRangeIsSwapped()
    {
        Assert.Equal((120d, 120d), (SearchQueryParser.Parse("bpm:120").BpmMin, SearchQueryParser.Parse("bpm:120").BpmMax));
        var q = SearchQueryParser.Parse("bpm:125-115");
        Assert.Equal((115d, 125d), (q.BpmMin, q.BpmMax));
    }

    [Fact]
    public void MissingFilters_AreCollected()
    {
        var q = SearchQueryParser.Parse("missing:title missing:bpm");
        Assert.Equal(["title", "bpm"], q.Missing);
    }

    [Theory]
    [InlineData("bpm:abc")]
    [InlineData("year:x-y")]
    [InlineData("decade:zz")]
    [InlineData("missing:banana")]
    [InlineData("artist:")]
    [InlineData("played:sometimes")]
    [InlineData("played:1")]
    [InlineData("played:")]
    public void InvalidExpressions_WarnWithoutThrowing(string input)
    {
        var q = SearchQueryParser.Parse(input);
        Assert.Single(q.Warnings);
    }

    [Theory]
    [InlineData("played:never", MusicFinder.Domain.Enums.PlayedFilter.Never)]
    [InlineData("played:session", MusicFinder.Domain.Enums.PlayedFilter.Session)]
    [InlineData("played:possible", MusicFinder.Domain.Enums.PlayedFilter.Possible)]
    [InlineData("played:confirmed", MusicFinder.Domain.Enums.PlayedFilter.Confirmed)]
    [InlineData("PLAYED:Never", MusicFinder.Domain.Enums.PlayedFilter.Never)]
    public void PlayedFilter_IsParsed(string input, MusicFinder.Domain.Enums.PlayedFilter expected)
    {
        var q = SearchQueryParser.Parse(input);
        Assert.Equal(expected, q.Played);
        Assert.Empty(q.Warnings);
    }

    [Fact]
    public void PlayedFilter_CombinesWithTextAndOtherFilters()
    {
        var q = SearchQueryParser.Parse("disco played:never bpm:120-125");
        Assert.Equal(MusicFinder.Domain.Enums.PlayedFilter.Never, q.Played);
        Assert.Equal([new TextTerm(null, "disco", false)], q.Terms);
        Assert.Equal((120d, 125d), (q.BpmMin, q.BpmMax));
    }

    [Fact]
    public void UnknownFieldPrefix_IsLiteralText()
    {
        var q = SearchQueryParser.Parse("http://x foo:bar");
        Assert.Equal([new TextTerm(null, "http://x", false), new TextTerm(null, "foo:bar", false)], q.Terms);
    }

    [Theory]
    [InlineData("\"unterminated phrase")]
    [InlineData("\"")]
    [InlineData("artist:\"")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("((( ))) *** : ::")]
    public void Garbage_NeverThrows(string? input) => SearchQueryParser.Parse(input);
}
