using MusicFinder.Domain.Enums;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure.Search;

namespace MusicFinder.IntegrationTests;

/// <summary>
/// CLAUDE.md section 12E on a real database with the alias index built: Greek, Greeklish and wrong-keyboard-layout queries find each
/// other, exact matches rank first, and nothing in the query text can break the FTS expression.
/// Everything that goes through the phonetic key is heuristic and is marked so in the table of docs/greek-search.md.
/// </summary>
public class SearchGreekTests
{
    private sealed class Env : IAsyncDisposable
    {
        public TestDb Db { get; } = new();
        public SqliteSearchService Search { get; }

        public Env() => Search = new SqliteSearchService(Db.Factory);

        public static async Task<Env> CreateAsync(bool buildIndex = true)
        {
            var e = new Env();
            var root = await e.Db.Roots.AddAsync(@"C:\Music");
            var folder = await e.Db.Folders.UpsertAsync(root.Id, null, @"C:\Music", "Music");
            await e.Db.Tracks.UpsertBatchAsync(
            [
                TestDb.MakeTrack(folder, @"C:\Music\Λαϊκά\Χάρις Αλεξίου - Σ' αγαπώ.mp3", "Σ' αγαπώ", "Χάρις Αλεξίου", "Λαϊκά"),
                TestDb.MakeTrack(folder, @"C:\Music\Μαζωνάκης - Παπάζογλου.mp3", "Παπάζογλου", "Γιώργος Μαζωνάκης", "Pop"),
                TestDb.MakeTrack(folder, @"C:\Music\Nikos Papazoglou - Ah Ellada.mp3", "Αχ Ελλάδα", "Nikos Papazoglou", "Rock"),
                TestDb.MakeTrack(folder, @"C:\Music\Pop\Chic - Le Freak.mp3", "Le Freak", "Chic", "Disco"),
                TestDb.MakeTrack(folder, @"C:\Music\Xaris Alexiou - Dimitroula.mp3", "Dimitroula", "Xaris Alexiou", "Pop"),
                TestDb.MakeTrack(folder, @"C:\Music\The Police - Every Breath You Take.mp3", "Every Breath You Take", "The Police", "Rock"),
                TestDb.MakeTrack(folder, @"C:\Music\Καλημέρα.mp3", "Καλημέρα", "Δημήτρης Μητροπάνος", "Pop"),
            ]);
            if (buildIndex) await e.Db.AliasJob.RunAsync();
            return e;
        }

        public async Task<List<string>> TitlesAsync(string text, SortField sort = SortField.Relevance) =>
            (await Search.SearchAsync(new SearchQuery { Text = text, Sort = sort, GlobalScope = true, PageSize = 100 }, default))
            .Items.Select(i => i.Title).ToList();

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private const string Agapo = "Σ' αγαπώ";
    private const string Papazoglou = "Παπάζογλου";   // the track whose TITLE is Παπάζογλου (artist Μαζωνάκης)
    private const string AhEllada = "Αχ Ελλάδα";      // artist Nikos Papazoglou (Latin)

    // ===== the track is found by every spelling, and exact comes first =====

    [Fact]
    public async Task GreekQuery_FindsTheGreekTitleFirst_ThenTheLatinSpelledArtist()
    {
        await using var e = await Env.CreateAsync();

        Assert.Equal([Papazoglou, AhEllada], await e.TitlesAsync("Παπάζογλου"));
        Assert.Equal([Papazoglou, AhEllada], await e.TitlesAsync("παπαζογλου"));   // without the accent: normalized tier, same set
    }

    [Fact]
    public async Task LatinQuery_FindsTheLatinArtistFirst_ThenTheGreekTitle()
    {
        await using var e = await Env.CreateAsync();

        Assert.Equal([AhEllada, Papazoglou], await e.TitlesAsync("papazoglou"));
        Assert.Equal([AhEllada, Papazoglou], await e.TitlesAsync("papaz"));        // prefix works for both scripts
    }

    [Fact]
    public async Task ExactGreekArtist_RanksBeforeItsGreeklishSpelling()
    {
        await using var e = await Env.CreateAsync();

        Assert.Equal([Agapo, "Dimitroula"], await e.TitlesAsync("Χάρις Αλεξίου"));
        Assert.Equal(["Dimitroula", Agapo], await e.TitlesAsync("xaris alexiou"));
    }

    [Theory]
    [InlineData("haris alexiou")]
    [InlineData("charis alexiou")]
    [InlineData("xaris aleksiou")]
    [InlineData("χάρις aleksiou")]       // mixed scripts in one query
    [InlineData("xaris Αλεξίου")]
    [InlineData("alexiou xaris")]        // word order does not matter
    [InlineData("alex xar")]             // prefixes of both words
    public async Task EveryGreeklishSpelling_FindsBothAlexiouTracks(string query)
    {
        await using var e = await Env.CreateAsync();

        Assert.Equal([Agapo, "Dimitroula"], (await e.TitlesAsync(query)).Order().ToList());
    }

    [Theory]
    [InlineData("Αλεξίου")]
    [InlineData("αλεξιου")]
    [InlineData("ΑΛΕΞΙΟΥ")]
    [InlineData("αλεξίου")]
    public async Task CaseAndAccents_DoNotChangeTheResult(string query)
    {
        await using var e = await Env.CreateAsync();

        Assert.Equal([Agapo, "Dimitroula"], (await e.TitlesAsync(query)).Order().ToList());
    }

    [Fact]
    public async Task FinalSigma_IsTheSameLetterAsSigma()
    {
        await using var e = await Env.CreateAsync();

        Assert.Equal(["Καλημέρα"], await e.TitlesAsync("Μητροπάνος"));
        Assert.Equal(["Καλημέρα"], await e.TitlesAsync("μητροπανοσ"));    // σ at the end
        Assert.Equal(["Καλημέρα"], await e.TitlesAsync("ΜΗΤΡΟΠΑΝΟΣ"));
    }

    [Theory]
    [InlineData("Μαζωνάκης")]
    [InlineData("ΜΑΖΩΝΑΚΗΣ")]
    [InlineData("mazonakis")]
    [InlineData("mazwnakhs")]
    [InlineData("Maz")]
    [InlineData("giorgos mazonakis")]
    [InlineData("yorgos mazonakis")]
    [InlineData("Γιώργος")]
    public async Task Mazonakis_IsFoundUnderEverySpelling(string query)
    {
        await using var e = await Env.CreateAsync();

        Assert.Contains(Papazoglou, await e.TitlesAsync(query));
    }

    [Theory]
    [InlineData("kalhmera")]
    [InlineData("kalimera")]
    [InlineData("Καλημέρα")]
    [InlineData("καλημερα")]
    public async Task ATitle_IsFoundBySpellingItInGreeklish(string query)
    {
        await using var e = await Env.CreateAsync();

        Assert.Equal(["Καλημέρα"], await e.TitlesAsync(query));
    }

    // ===== wrong keyboard layout, both directions =====

    [Fact]
    public async Task GreekTypedOnAUsKeyboard_IsFound()
    {
        await using var e = await Env.CreateAsync();

        Assert.Equal([Agapo, "Dimitroula"], (await e.TitlesAsync("x;aris")).Order().ToList());   // ; is the tonos dead key: Χάρις
        Assert.Equal([Agapo, "Dimitroula"], (await e.TitlesAsync("alej;ioy")).Order().ToList());  // Αλεξίου (ου is o+y on this layout)
        Assert.Equal(["Καλημέρα"], await e.TitlesAsync("kal;hmer;a"));
    }

    [Fact]
    public async Task LatinTypedOnAGreekKeyboard_IsFound()
    {
        await using var e = await Env.CreateAsync();

        Assert.Equal(["Le Freak"], await e.TitlesAsync("ψηιψ"));          // "chic"
        Assert.Contains("Every Breath You Take", await e.TitlesAsync("εωερυ"));    // "every" typed with the Greek layout active
    }

    // ===== precision: heuristics must not drown plain matches =====

    [Fact]
    public async Task AnEnglishQuery_ReturnsOnlyTheEnglishTrack()
    {
        await using var e = await Env.CreateAsync();

        Assert.Equal(["Every Breath You Take"], await e.TitlesAsync("every breath"));
        Assert.Equal(["Le Freak"], await e.TitlesAsync("le freak"));
        Assert.Equal(["Le Freak"], await e.TitlesAsync("\"le freak\""));
        Assert.Empty(await e.TitlesAsync("\"freak le\""));                  // a phrase keeps its word order
    }

    [Fact]
    public async Task ADifferentName_IsNotFoundJustBecauseItSoundsAlike()
    {
        await using var e = await Env.CreateAsync();

        Assert.Empty(await e.TitlesAsync("zorba"));
        Assert.DoesNotContain("Le Freak", await e.TitlesAsync("sik"));       // sik is not the key of chic
    }

    [Theory]
    [InlineData("R&B")]
    [InlineData("it's")]
    [InlineData("AND")]
    [InlineData("OR")]
    [InlineData("NOT")]
    [InlineData("NEAR(")]
    [InlineData("*")]
    [InlineData("\"")]
    [InlineData("\"\"")]
    [InlineData("a:b")]
    [InlineData("title:")]
    [InlineData("(((")]
    [InlineData("^freak")]
    [InlineData("freak*")]
    [InlineData("-freak")]
    [InlineData("Σ'")]
    [InlineData("x;")]
    [InlineData(";;;")]
    [InlineData("é")]
    [InlineData("🎵")]
    public async Task HostileOrOddInput_NeverThrows(string query)
    {
        await using var e = await Env.CreateAsync();

        var page = await e.Search.SearchAsync(new SearchQuery { Text = query, Sort = SortField.Relevance }, default);
        var count = await e.Search.CountAsync(new SearchQuery { Text = query }, default);

        Assert.True(count >= 0);
        Assert.NotNull(page.Items);
    }

    [Fact]
    public async Task APunctuationQuery_ThatContainsAWord_StillFindsIt()
    {
        await using var e = await Env.CreateAsync();

        Assert.Equal(["Le Freak"], await e.TitlesAsync("freak!"));
        Assert.Equal(["Le Freak"], await e.TitlesAsync("(freak)"));
        Assert.Equal([Agapo], (await e.TitlesAsync("Σ' αγαπώ")).Take(1).ToList());
    }

    // ===== field filters use the same pipeline =====

    [Fact]
    public async Task ScopedFilters_UseTheSameNormalization()
    {
        await using var e = await Env.CreateAsync();

        Assert.Equal([Agapo, "Dimitroula"], (await e.TitlesAsync("artist:xaris")).Order().ToList());
        Assert.Equal([Agapo, "Dimitroula"], (await e.TitlesAsync("artist:Αλεξίου")).Order().ToList());
        Assert.Equal([Agapo, "Dimitroula"], (await e.TitlesAsync("artist:αλεξιου")).Order().ToList());
        Assert.Equal([Papazoglou], await e.TitlesAsync("title:papaz"));
        Assert.Empty(await e.TitlesAsync("title:xaris"));                  // xaris is an artist, not a title
        Assert.Equal([Agapo], await e.TitlesAsync("genre:λαικα"));
        Assert.Equal([Agapo], await e.TitlesAsync("genre:laika"));
    }

    [Fact]
    public async Task ScopedFiltersCombineWithPlainWords()
    {
        await using var e = await Env.CreateAsync();

        Assert.Equal(["Dimitroula"], await e.TitlesAsync("artist:xaris dimitroula"));
        Assert.Empty(await e.TitlesAsync("artist:xaris freak"));
    }

    [Fact]
    public async Task PathNames_AreSearchableInBothScripts()
    {
        await using var e = await Env.CreateAsync();

        Assert.Contains(Agapo, await e.TitlesAsync("λαικα"));              // the folder is called Λαϊκά
        Assert.Contains(Agapo, await e.TitlesAsync("laika"));
    }

    // ===== ranking =====

    [Fact]
    public async Task AMatchInTitleOrArtist_RanksBeforeAMatchOnlyInThePath()
    {
        await using var e = await Env.CreateAsync();
        var root = (await e.Db.Roots.GetAllAsync()).Single();
        var folder = await e.Db.Folders.UpsertAsync(root.Id, null, @"C:\Music\freak", "freak");
        await e.Db.Tracks.UpsertBatchAsync([TestDb.MakeTrack(folder, @"C:\Music\freak\aaa.mp3", "Aaa First By Title", "Nobody", "Pop")]);
        await e.Db.AliasJob.RunAsync();

        var titles = await e.TitlesAsync("freak");

        Assert.Equal(["Le Freak", "Aaa First By Title"], titles);          // alphabetically Aaa would win; relevance puts the real title first
    }

    [Fact]
    public async Task RelevanceOrder_IsDeterministic_WithTitleAsTheTieBreak()
    {
        await using var e = await Env.CreateAsync();

        var first = await e.TitlesAsync("xaris");
        var second = await e.TitlesAsync("xaris");

        Assert.Equal(first, second);
        Assert.Equal(["Dimitroula", Agapo], first);
    }

    [Fact]
    public async Task AnExplicitColumnSort_IsNotAffectedByRelevance()
    {
        await using var e = await Env.CreateAsync();

        var byTitle = await e.TitlesAsync("xaris", SortField.Title);

        Assert.Equal(["Dimitroula", Agapo], byTitle);                      // D before Σ
        Assert.Equal(2, await e.Search.CountAsync(new SearchQuery { Text = "xaris" }, default));
    }

    [Fact]
    public async Task RelevanceWithNoText_IsTheTitleOrder()
    {
        await using var e = await Env.CreateAsync();

        Assert.Equal(await e.TitlesAsync("", SortField.Title), await e.TitlesAsync("", SortField.Relevance));
    }

    [Fact]
    public async Task RelevancePaging_WalksEveryRowOnce_AndMatchesTheCount()
    {
        await using var e = await Env.CreateAsync();
        const string text = "papaz";
        var seen = new List<long>();
        SearchCursor? cursor = null;
        do
        {
            var page = await e.Search.SearchAsync(new SearchQuery { Text = text, Sort = SortField.Relevance, PageSize = 1, After = cursor }, default);
            seen.AddRange(page.Items.Select(i => i.Id));
            cursor = page.Next;
        } while (cursor is not null);

        Assert.Equal(2, seen.Count);
        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Equal(seen.Count, await e.Search.CountAsync(new SearchQuery { Text = text }, default));
    }

    [Fact]
    public async Task RelevanceIsNeverReversed()
    {
        await using var e = await Env.CreateAsync();

        var asc = (await e.Search.SearchAsync(new SearchQuery { Text = "papazoglou", Sort = SortField.Relevance, Direction = SortDirection.Asc }, default)).Items.Select(i => i.Title);
        var desc = (await e.Search.SearchAsync(new SearchQuery { Text = "papazoglou", Sort = SortField.Relevance, Direction = SortDirection.Desc }, default)).Items.Select(i => i.Title);

        Assert.Equal(asc, desc);
    }

    // ===== while the alias index is still being built, nothing goes dark =====

    [Fact]
    public async Task BeforeTheIndexIsBuilt_PlainAndNormalizedSearchStillWork()
    {
        await using var e = await Env.CreateAsync(buildIndex: false);

        Assert.Equal([Papazoglou], await e.TitlesAsync("Παπάζογλου"));      // as before M7: the original index
        Assert.Equal([Papazoglou], await e.TitlesAsync("παπαζογλου"));
        Assert.Equal(["Le Freak"], await e.TitlesAsync("freak"));
        Assert.Empty(await e.TitlesAsync("xaris Αλεξίου"));                  // bridging the two spellings needs the index
    }

    [Fact]
    public async Task OnceTheIndexIsBuilt_TheSameQueryFindsMore()
    {
        await using var e = await Env.CreateAsync(buildIndex: false);
        var before = await e.Search.CountAsync(new SearchQuery { Text = "papazoglou" }, default);

        await e.Db.AliasJob.RunAsync();

        Assert.Equal(1, before);                                           // only the Latin-spelled artist
        Assert.Equal(2, await e.Search.CountAsync(new SearchQuery { Text = "papazoglou" }, default));
    }

    // ===== switching smart search off restores the old behaviour =====

    [Fact]
    public async Task WithSmartGreekOff_OnlyThePlainIndexIsUsed()
    {
        await using var e = await Env.CreateAsync();
        var settings = new FakeSettings { Value = { SmartGreekSearch = false } };
        var plain = new SqliteSearchService(e.Db.Factory, settings);

        var page = await plain.SearchAsync(new SearchQuery { Text = "papazoglou", Sort = SortField.Relevance }, default);

        Assert.Equal(["Αχ Ελλάδα"], page.Items.Select(i => i.Title));      // no Greeklish bridge
    }

    private sealed class FakeSettings : MusicFinder.Domain.Contracts.ISettingsStore
    {
        public AppSettings Value { get; } = new();
        public AppSettings Load() => Value;
        public void Save(AppSettings settings) { }
    }
}
