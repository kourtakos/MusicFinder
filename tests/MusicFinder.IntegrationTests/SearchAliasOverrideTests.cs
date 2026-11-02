using Dapper;
using MusicFinder.Application.Search;
using MusicFinder.Domain.Enums;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure.Search;

namespace MusicFinder.IntegrationTests;

/// <summary>The user's own search aliases and the "Explain a search" diagnostics.</summary>
public class SearchAliasOverrideTests
{
    private sealed class Env : IAsyncDisposable
    {
        public TestDb Db { get; } = new();
        public SqliteSearchService Search { get; }
        public SearchAliasService Aliases { get; }

        public Env()
        {
            Search = new SqliteSearchService(Db.Factory);
            Aliases = new SearchAliasService(Db.AliasOverrides, Db.Aliases, Db.AliasJob);
        }

        public static async Task<Env> CreateAsync()
        {
            var e = new Env();
            var root = await e.Db.Roots.AddAsync(@"C:\Music");
            var folder = await e.Db.Folders.UpsertAsync(root.Id, null, @"C:\Music", "Music");
            await e.Db.Tracks.UpsertBatchAsync(
            [
                TestDb.MakeTrack(folder, @"C:\Music\Daft Punk - One More Time.mp3", "One More Time", "Daft Punk", "Electronic"),
                TestDb.MakeTrack(folder, @"C:\Music\Chic - Le Freak.mp3", "Le Freak", "Chic", "Disco"),
                TestDb.MakeTrack(folder, @"C:\Music\Νταφτ.mp3", "Άλλο", "Νταφτ Πανκ", "Pop"),
            ]);
            await e.Db.AliasJob.RunAsync();
            return e;
        }

        public async Task<List<string>> TitlesAsync(string text) =>
            (await Search.SearchAsync(new SearchQuery { Text = text, Sort = SortField.Relevance }, default)).Items.Select(i => i.Title).ToList();

        public int StaleCount()
        {
            using var conn = Db.Factory.Open();
            return conn.ExecuteScalar<int>("SELECT COUNT(*) FROM Tracks WHERE SearchAliasVersion = 0");
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    // ===== aliases =====

    [Fact]
    public async Task WithoutAnAlias_AGreekSpellingOfAnEnglishName_IsNotBridged()
    {
        await using var e = await Env.CreateAsync();

        // The automatic rules cannot know that "νταφτ πανκ" is "Daft Punk": the vowels differ (a/u vs α/ο...).
        Assert.DoesNotContain("One More Time", await e.TitlesAsync("νταφτ πανκ"));
    }

    [Fact]
    public async Task AnAlias_MakesBothSpellingsFindTheTrack_InBothDirections()
    {
        await using var e = await Env.CreateAsync();
        var added = await e.Aliases.AddAsync("Daft Punk", "ντάφτ πανκ");
        Assert.True(added.Success, added.Error);

        Assert.Contains("One More Time", await e.TitlesAsync("ντάφτ πανκ"));          // the Greek spelling now finds the English track
        Assert.Contains("One More Time", await e.TitlesAsync("ντάφτ"));                // a prefix of it too
        Assert.Contains("One More Time", await e.TitlesAsync("ΝΤΑΦΤ"));                // case and accents do not matter
        Assert.Contains("One More Time", await e.TitlesAsync("daft punk"));            // the original still works
        Assert.DoesNotContain("Le Freak", await e.TitlesAsync("ντάφτ πανκ"));          // and it does not leak to other tracks
    }

    [Fact]
    public async Task AnAlias_IsAlsoFoundByItsGreeklishKey()
    {
        await using var e = await Env.CreateAsync();
        await e.Aliases.AddAsync("Chic", "σικ");

        Assert.Contains("Le Freak", await e.TitlesAsync("σικ"));
        Assert.Contains("Le Freak", await e.TitlesAsync("sik"));                       // Greeklish for the alias
    }

    [Fact]
    public async Task AddingAnAlias_RebuildsOnlyTheTracksItAppliesTo()
    {
        await using var e = await Env.CreateAsync();
        Assert.Equal(0, e.StaleCount());

        await e.Aliases.AddAsync("Chic", "σικ");

        Assert.Equal(0, e.StaleCount());                                              // everything is current again
        using var conn = e.Db.Factory.Open();
        Assert.Equal(1, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM TrackSearchAliases WHERE Extra <> ''"));   // only Chic carries it
    }

    [Fact]
    public async Task RemovingAnAlias_StopsTheMatch_AndLeavesTheOthers()
    {
        await using var e = await Env.CreateAsync();
        var first = await e.Aliases.AddAsync("Chic", "σικ");
        await e.Aliases.AddAsync("Daft Punk", "ντάφτ πανκ");
        Assert.Contains("Le Freak", await e.TitlesAsync("σικ"));

        await e.Aliases.RemoveAsync(first.Alias!.Id);

        Assert.DoesNotContain("Le Freak", await e.TitlesAsync("σικ"));
        Assert.Contains("One More Time", await e.TitlesAsync("ντάφτ πανκ"));
        Assert.Single(await e.Aliases.GetAllAsync());
    }

    [Fact]
    public async Task AnAlias_AppliesToNewTracksAddedLater()
    {
        await using var e = await Env.CreateAsync();
        await e.Aliases.AddAsync("Daft Punk", "ντάφτ πανκ");
        var root = (await e.Db.Roots.GetAllAsync()).Single();
        var folder = await e.Db.Folders.UpsertAsync(root.Id, null, @"C:\Music", "Music");

        await e.Db.Tracks.UpsertBatchAsync([TestDb.MakeTrack(folder, @"C:\Music\dp2.mp3", "Around the World", "Daft Punk", "Electronic")]);
        await e.Db.AliasJob.RunAsync();

        Assert.Contains("Around the World", await e.TitlesAsync("ντάφτ"));
    }

    [Fact]
    public async Task AliasesChanged_IsRaised()
    {
        await using var e = await Env.CreateAsync();
        var raised = 0;
        e.Aliases.AliasesChanged += (_, _) => raised++;

        var added = await e.Aliases.AddAsync("Chic", "σικ");
        await e.Aliases.RemoveAsync(added.Alias!.Id);
        await e.Aliases.RemoveAsync(added.Alias.Id);                                  // already gone: nothing changes

        Assert.Equal(2, raised);
    }

    [Theory]
    [InlineData("", "σικ", "Enter both")]
    [InlineData("Chic", "", "Enter both")]
    [InlineData("  ", "  ", "Enter both")]
    [InlineData("...", "σικ", "Enter both")]                       // nothing searchable once punctuation is dropped
    [InlineData("Chic", "CHIC", "same once case")]
    [InlineData("Chic", "Chíc", "same once case")]
    public async Task InvalidAliases_AreExplained_AndNothingIsStored(string source, string alias, string expected)
    {
        await using var e = await Env.CreateAsync();

        var result = await e.Aliases.AddAsync(source, alias);

        Assert.False(result.Success);
        Assert.Contains(expected, result.Error);
        Assert.Empty(await e.Aliases.GetAllAsync());
    }

    [Fact]
    public async Task ADuplicateAlias_IsRefused()
    {
        await using var e = await Env.CreateAsync();
        await e.Aliases.AddAsync("Chic", "σικ");

        var again = await e.Aliases.AddAsync("CHIC", "σικ");

        Assert.False(again.Success);
        Assert.Contains("already exists", again.Error);
        Assert.Single(await e.Aliases.GetAllAsync());
    }

    [Fact]
    public async Task AVeryLongAlias_IsRefused()
    {
        await using var e = await Env.CreateAsync();

        Assert.False((await e.Aliases.AddAsync(new string('a', 101), "σικ")).Success);
    }

    // ===== explain =====

    [Fact]
    public async Task Explain_ShowsEveryFormOfEveryWord()
    {
        await using var e = await Env.CreateAsync();

        var explanation = await e.Search.ExplainAsync("xaris alexiou");

        Assert.Equal(2, explanation.Terms.Count);
        var xaris = explanation.Terms[0].Forms.ToDictionary(f => f.Kind, f => f.Value);
        Assert.Equal("xaris", xaris["as typed"]);
        Assert.Equal("xaris", xaris["normalized"]);
        Assert.Equal("χαρισ", xaris["other keyboard layout"]);          // the same physical keys on the Greek layout
        Assert.Equal("xaris", xaris["phonetic key"]);
        Assert.Contains(explanation.Terms[1].Forms, f => f.Kind == "phonetic key" && f.Value == "alexiu");
    }

    [Fact]
    public async Task Explain_SaysWhichTierEachResultMatchedAt()
    {
        await using var e = await Env.CreateAsync();

        var explanation = await e.Search.ExplainAsync("daft");

        var daft = explanation.Results.First(r => r.Title == "One More Time");
        Assert.Equal(0, daft.Tier);                                    // as typed
        Assert.True(daft.InTitleArtistAlbum);
        Assert.True(explanation.Results.Count <= explanation.ResultLimit);
    }

    [Fact]
    public async Task Explain_OfAGreeklishQuery_ShowsThePhoneticTier()
    {
        await using var e = await Env.CreateAsync();
        var root = (await e.Db.Roots.GetAllAsync()).Single();
        var folder = await e.Db.Folders.UpsertAsync(root.Id, null, @"C:\Music", "Music");
        await e.Db.Tracks.UpsertBatchAsync([TestDb.MakeTrack(folder, @"C:\Music\x.mp3", "Καλημέρα", "Δημήτρης Μητροπάνος", "Pop")]);
        await e.Db.AliasJob.RunAsync();

        var explanation = await e.Search.ExplainAsync("kalimera");

        Assert.Equal([3], explanation.Results.Select(r => r.Tier));    // matched only through the phonetic key
    }

    [Fact]
    public async Task Explain_OfNothingSearchable_IsEmptyAndDoesNotThrow()
    {
        await using var e = await Env.CreateAsync();

        var explanation = await e.Search.ExplainAsync("!!!");

        Assert.Empty(explanation.Terms);
        Assert.Empty(explanation.Results);
    }

    [Fact]
    public async Task Explain_ReportsQueryWarnings()
    {
        await using var e = await Env.CreateAsync();

        var explanation = await e.Search.ExplainAsync("bpm:abc daft");

        Assert.NotEmpty(explanation.Warnings);
        Assert.Contains(explanation.Results, r => r.Title == "One More Time");
    }
}
