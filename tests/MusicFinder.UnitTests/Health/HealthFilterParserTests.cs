using MusicFinder.Application.Search;

namespace MusicFinder.UnitTests.Health;

public class HealthFilterParserTests
{
    [Theory]
    [InlineData("missing:art")]
    [InlineData("missing:cover")]
    public void MissingArtwork_IsAFilter(string text)
    {
        var q = SearchQueryParser.Parse(text);
        Assert.Single(q.Missing);
        Assert.Empty(q.Warnings);
    }

    [Theory]
    [InlineData("issue:mojibake")]
    [InlineData("issue:error")]
    [InlineData("issue:lowbitrate")]
    [InlineData("issue:samplerate")]
    [InlineData("issue:mismatch")]
    [InlineData("issue:size")]
    [InlineData("issue:duration")]
    [InlineData("issue:any")]
    [InlineData("ISSUE:Any")]
    public void KnownIssues_AreAccepted(string text)
    {
        var q = SearchQueryParser.Parse(text);
        Assert.Single(q.Issues);
        Assert.Empty(q.Warnings);
    }

    [Fact]
    public void AnUnknownIssue_WarnsAndFiltersNothing()
    {
        var q = SearchQueryParser.Parse("issue:nonsense chic");
        Assert.Empty(q.Issues);
        Assert.Single(q.Warnings);
        Assert.Single(q.Terms);
    }

    [Theory]
    [InlineData("bitrate:320", 320, 320)]
    [InlineData("bitrate:0-127", 0, 127)]
    [InlineData("bitrate:192-128", 128, 192)]
    public void BitrateRanges_AreParsed(string text, double min, double max)
    {
        var q = SearchQueryParser.Parse(text);
        Assert.Equal((min, max), (q.BitrateMin, q.BitrateMax));
    }

    [Fact]
    public void ABadBitrate_Warns() => Assert.Single(SearchQueryParser.Parse("bitrate:fast").Warnings);

    [Fact]
    public void ACodec_IsKept() => Assert.Equal("flac", SearchQueryParser.Parse("codec:flac").Codec);

    [Fact]
    public void TheFiltersCombineWithText()
    {
        var q = SearchQueryParser.Parse("chic issue:lowbitrate missing:art");
        Assert.Single(q.Terms);
        Assert.Equal(["lowbitrate"], q.Issues);
        Assert.Single(q.Missing);
    }
}

public class DuplicateFilterParserTests
{
    [Theory]
    [InlineData("dup:exact", "exact")]
    [InlineData("dup:any", "exact")]
    [InlineData("DUP:Exact", "exact")]
    [InlineData("dup:0a1b2c3d4e5f6a7b", "0A1B2C3D4E5F6A7B")]
    public void DupFilters_AreParsed(string text, string expected)
    {
        var q = MusicFinder.Application.Search.SearchQueryParser.Parse(text);
        Assert.Equal(expected, q.Duplicate);
        Assert.Empty(q.Warnings);
    }

    [Theory]
    [InlineData("dup:")]
    [InlineData("dup:nonsense")]
    [InlineData("dup:abc")]          // too short to name a group
    [InlineData("dup:zzzzzzzz")]     // not hexadecimal
    public void BadDupValues_WarnAndFilterNothing(string text)
    {
        var q = MusicFinder.Application.Search.SearchQueryParser.Parse(text);
        Assert.Null(q.Duplicate);
        Assert.Single(q.Warnings);
    }
}
