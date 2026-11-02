using MusicFinder.Domain.Enums;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure.Search;

namespace MusicFinder.IntegrationTests;

/// <summary>Bounded typo correction: only against real words of the library, only when nothing starts with what was typed.</summary>
public class SearchFuzzyTests
{
    private sealed class FakeSettings : MusicFinder.Domain.Contracts.ISettingsStore
    {
        public AppSettings Value { get; } = new();
        public AppSettings Load() => Value;
        public void Save(AppSettings settings) { }
    }

    private sealed class Env : IAsyncDisposable
    {
        public TestDb Db { get; } = new();
        public FtsVocabulary Vocabulary { get; }
        public FakeSettings Settings { get; } = new();
        public SqliteSearchService Search { get; }

        public Env()
        {
            Vocabulary = new FtsVocabulary(Db.Factory);
            Search = new SqliteSearchService(Db.Factory, Settings, Vocabulary);
        }

        public static async Task<Env> CreateAsync(bool loadVocabulary = true)
        {
            var e = new Env();
            var root = await e.Db.Roots.AddAsync(@"C:\Music");
            var folder = await e.Db.Folders.UpsertAsync(root.Id, null, @"C:\Music", "Music");
            await e.Db.Tracks.UpsertBatchAsync(
            [
                TestDb.MakeTrack(folder, @"C:\Music\a.mp3", "Αχ Ελλάδα", "Nikos Papazoglou", "Rock"),
                TestDb.MakeTrack(folder, @"C:\Music\b.mp3", "Le Freak", "Chic", "Disco"),
                TestDb.MakeTrack(folder, @"C:\Music\c.mp3", "Σ' αγαπώ", "Χάρις Αλεξίου", "Λαϊκά"),
                TestDb.MakeTrack(folder, @"C:\Music\d.mp3", "Billie Jean", "Michael Jackson", "Pop"),
            ]);
            await e.Db.AliasJob.RunAsync();
            if (loadVocabulary) await e.Vocabulary.EnsureLoadedAsync();
            return e;
        }

        public async Task<List<string>> TitlesAsync(string text) =>
            (await Search.SearchAsync(new SearchQuery { Text = text, Sort = SortField.Relevance, PageSize = 50 }, default)).Items.Select(i => i.Title).ToList();

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    // ===== edit distance =====

    [Theory]
    [InlineData("papazoglou", "papazoglou", 0)]
    [InlineData("papzoglou", "papazoglou", 1)]      // a missing letter
    [InlineData("papazogloy", "papazoglou", 1)]     // a wrong letter
    [InlineData("papaazoglou", "papazoglou", 1)]    // an extra letter
    [InlineData("papaozglou", "papazoglou", 1)]     // two neighbours swapped is ONE mistake
    [InlineData("pappzogluu", "papazoglou", 2)]
    public void EditDistance_CountsInsertDeleteSubstituteAndSwap(string a, string b, int expected) =>
        Assert.Equal(expected, FtsVocabulary.EditDistance(a, b, limit: 5));

    [Fact]
    public void EditDistance_GivesUpPastTheLimit()
    {
        Assert.True(FtsVocabulary.EditDistance("abcdefgh", "zyxwvuts", 2) > 2);
        Assert.True(FtsVocabulary.EditDistance("a", "abcdef", 2) > 2);                // length alone rules it out
    }

    // ===== the vocabulary =====

    [Fact]
    public async Task TheVocabulary_ListsTheWordsOfTheIndexedFields()
    {
        await using var e = await Env.CreateAsync();

        Assert.True(e.Vocabulary.IsLoaded);
        Assert.True(e.Vocabulary.HasPrefix("papazo"));
        Assert.True(e.Vocabulary.HasPrefix("αλε"));                                     // from Αλεξίου
        Assert.True(e.Vocabulary.HasPrefix("chic"));
        Assert.False(e.Vocabulary.HasPrefix("qqq"));
        Assert.False(e.Vocabulary.HasPrefix("papazx"));
    }

    [Fact]
    public async Task Nearest_ReturnsCloseRealWordsOnly_BestFirst()
    {
        await using var e = await Env.CreateAsync();

        Assert.Equal(["papazoglou"], e.Vocabulary.Nearest("papzoglou", 3));
        Assert.Equal(["jackson"], e.Vocabulary.Nearest("jacksan", 3));
        Assert.Empty(e.Vocabulary.Nearest("zzzzzzzz", 3));
        Assert.Empty(e.Vocabulary.Nearest("papazoglou", 0));                         // an exact word has no "nearest": it IS the word
    }

    [Fact]
    public async Task BeforeTheVocabularyIsLoaded_NothingIsCorrected()
    {
        await using var e = await Env.CreateAsync(loadVocabulary: false);

        Assert.False(e.Vocabulary.IsLoaded);
        Assert.True(e.Vocabulary.HasPrefix("anything"));                              // "unknown" is treated as "fine"
        Assert.Empty(e.Vocabulary.Nearest("papzoglou", 3));
        Assert.Empty(await e.TitlesAsync("papzoglou"));
    }

    [Fact]
    public async Task InvalidatingTheVocabulary_MakesItReloadWithNewWords()
    {
        await using var e = await Env.CreateAsync();
        var root = (await e.Db.Roots.GetAllAsync()).Single();
        var folder = await e.Db.Folders.UpsertAsync(root.Id, null, @"C:\Music", "Music");
        await e.Db.Tracks.UpsertBatchAsync([TestDb.MakeTrack(folder, @"C:\Music\e.mp3", "Thriller", "Michael Jackson", "Pop")]);
        await e.Db.AliasJob.RunAsync();

        Assert.False(e.Vocabulary.HasPrefix("thriller"));                             // not loaded yet: still the old list
        e.Vocabulary.Invalidate();
        Assert.False(e.Vocabulary.IsLoaded);
        await e.Vocabulary.EnsureLoadedAsync();

        Assert.True(e.Vocabulary.HasPrefix("thriller"));
    }

    // ===== search =====

    [Fact]
    public async Task ATypo_FindsTheTrack_AndRanksItBelowRealMatches()
    {
        await using var e = await Env.CreateAsync();

        Assert.Equal(["Αχ Ελλάδα"], await e.TitlesAsync("papzoglou"));
        Assert.Equal(["Billie Jean"], await e.TitlesAsync("jacksan"));
    }

    [Fact]
    public async Task AWordThatExists_IsNeverCorrected()
    {
        await using var e = await Env.CreateAsync();

        // "chic" is a real word, so it is searched as typed; "chi" is a prefix of it, so it is also left alone.
        Assert.Equal(["Le Freak"], await e.TitlesAsync("chic"));
        Assert.Equal(["Le Freak"], await e.TitlesAsync("chi"));
    }

    [Fact]
    public async Task TwoMistakes_AreOnlyForgivenInLongWords()
    {
        await using var e = await Env.CreateAsync();
        var root = (await e.Db.Roots.GetAllAsync()).Single();
        var folder = await e.Db.Folders.UpsertAsync(root.Id, null, @"C:\Music", "Music");
        await e.Db.Tracks.UpsertBatchAsync([TestDb.MakeTrack(folder, @"C:\Music\k.mp3", "Kamera", "Wilco", "Rock")]);
        await e.Db.AliasJob.RunAsync();
        await e.Vocabulary.EnsureLoadedAsync();
        e.Vocabulary.Invalidate();
        await e.Vocabulary.EnsureLoadedAsync();

        // Seen on the real library: the Greeklish greeting "kalhmera" is two letters away from "kamera"; it must not become that track.
        Assert.Empty(await e.TitlesAsync("kalhmera"));
        Assert.Empty(e.Vocabulary.Nearest("kalhmera", 3));
        // Nine letters or more: a second slip is still unmistakable.
        Assert.Contains("papazoglou", e.Vocabulary.Nearest("papzogglou", 3));
    }

    [Fact]
    public async Task ShortWords_AreNotCorrected()
    {
        await using var e = await Env.CreateAsync();

        Assert.Empty(await e.TitlesAsync("chk"));          // 3 letters: too short to guess from
        Assert.Empty(await e.TitlesAsync("jqqn"));         // 4 letters, but nothing within one mistake
        Assert.Equal(["Billie Jean"], await e.TitlesAsync("jeen"));   // one letter from a real word: corrected
    }

    [Fact]
    public async Task PhrasesAreNotCorrected()
    {
        await using var e = await Env.CreateAsync();

        Assert.Empty(await e.TitlesAsync("\"papzoglou\""));
    }

    [Fact]
    public async Task WithTypoMatchingOff_NothingIsCorrected()
    {
        await using var e = await Env.CreateAsync();
        e.Settings.Value.FuzzySearch = false;
        await Task.Delay(1100);                            // the service re-reads its switches at most once a second

        Assert.Empty(await e.TitlesAsync("papzoglou"));
    }

    [Fact]
    public async Task ATypoInOneWord_StillRequiresTheOtherWords()
    {
        await using var e = await Env.CreateAsync();

        Assert.Equal(["Αχ Ελλάδα"], await e.TitlesAsync("nikos papzoglou"));
        Assert.Empty(await e.TitlesAsync("chic papzoglou"));
    }
}
