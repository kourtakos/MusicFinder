using MusicFinder.Application.Search;
using MusicFinder.Infrastructure.Search;

namespace MusicFinder.UnitTests;

public class QueryExpanderTests
{
    private sealed class FakeVocabulary(params string[] words) : IVocabularyLookup
    {
        public int NearestCalls;
        public bool HasPrefix(string normalizedPrefix) => words.Any(w => w.StartsWith(normalizedPrefix, StringComparison.Ordinal));
        public IReadOnlyList<string> Nearest(string normalizedWord, int max)
        {
            NearestCalls++;
            return words.Where(w => FtsVocabulary.EditDistance(normalizedWord, w, 1) <= 1 && w != normalizedWord).Take(max).ToList();
        }
    }

    private static ExpandedQuery Expand(string text, ExpansionOptions? options = null, IVocabularyLookup? vocabulary = null) =>
        QueryExpander.Expand(SearchQueryParser.Parse(text), options ?? new ExpansionOptions(), vocabulary);

    // ===== forms of one word =====

    [Fact]
    public void ALatinWord_GetsItsGreekKeyboardReading_AndItsPhoneticKey()
    {
        var t = Expand("xaris").Terms.Single();

        Assert.Equal(("xaris", "xaris"), (t.Raw, t.N));
        Assert.Equal("χαρισ", t.KB);                    // the same physical keys on the Greek layout
        Assert.Equal("xaris", t.K);
        Assert.Null(t.KBK);                              // its key equals K: nothing new to search
        Assert.Empty(t.Fuzzy);
    }

    [Fact]
    public void AGreekWord_GetsItsLatinKeyboardReading_AndItsPhoneticKey()
    {
        var t = Expand("Χάρις").Terms.Single();

        Assert.Equal("χαρισ", t.N);
        Assert.Equal("xariw", t.KB);                     // ς is the W key
        Assert.Equal("xaris", t.K);                      // so Greeklish spellings of the name meet it
        Assert.NotNull(t.KBK);
    }

    [Fact]
    public void TheKeyIsKept_EvenWhenItEqualsTheNormalizedText()
    {
        // The K columns hold the keys of GREEK titles, so "kalimera" must be searched there to find Καλημέρα.
        var t = Expand("kalimera").Terms.Single();

        Assert.Equal("kalimera", t.N);
        Assert.Equal("kalimera", t.K);
    }

    [Fact]
    public void ADeadKeyWord_IsReadAsAccentedGreek()
    {
        var t = Expand("x;aris").Terms.Single();

        Assert.Equal("x aris", t.N);                     // as typed, ';' is just punctuation
        Assert.Equal("χαρισ", t.KB);                     // but as the tonos dead key it spells Χάρις
    }

    [Theory]
    [InlineData("sxt")]
    [InlineData("a")]
    [InlineData("1984")]
    [InlineData("AC/DC")]
    public void ImplausibleWords_GetNoKeyboardReading(string word) => Assert.Null(Expand(word).Terms.Single().KB);

    [Fact]
    public void PunctuationOnlyInput_HasNothingToSearch()
    {
        Assert.True(Expand("!!! ;;; ...").IsEmpty);
        Assert.True(Expand("").IsEmpty);
        Assert.True(Expand("   ").IsEmpty);
    }

    [Fact]
    public void FieldFiltersKeepTheirField_AndTheSameForms()
    {
        var t = Expand("artist:xaris").Terms.Single();

        Assert.Equal("artist", t.Field);
        Assert.Equal("χαρισ", t.KB);
    }

    [Fact]
    public void APhrase_IsMappedWordByWord()
    {
        var t = Expand("\"xaris alexiou\"").Terms.Single();

        Assert.True(t.IsPhrase);
        Assert.Equal("xaris alexiou", t.N);
        Assert.Equal("χαρισ αλεχιοθ", t.KB);             // each word, even though the second is not a real reading
        Assert.Equal("xaris alexiu", t.K);
    }

    // ===== switches =====

    [Fact]
    public void WithSmartGreekOff_OnlyTheNormalizedFormRemains()
    {
        var t = Expand("xaris", new ExpansionOptions { SmartGreek = false }).Terms.Single();

        Assert.Equal("xaris", t.N);
        Assert.Null(t.KB);
        Assert.Null(t.K);
        Assert.Null(t.KBK);
        Assert.Empty(t.Fuzzy);
    }

    // ===== typo candidates =====

    [Fact]
    public void ATypo_GetsCandidatesFromTheRealVocabulary()
    {
        var vocab = new FakeVocabulary("papazoglou", "chic");

        var t = Expand("papzoglou", vocabulary: vocab).Terms.Single();

        Assert.Equal(["papazoglou"], t.Fuzzy);
    }

    [Fact]
    public void AWordThatExistsOrIsBeingTyped_IsNeverCorrected()
    {
        var vocab = new FakeVocabulary("papazoglou");

        Assert.Empty(Expand("papaz", vocabulary: vocab).Terms.Single().Fuzzy);        // a prefix of a real word
        Assert.Empty(Expand("papazoglou", vocabulary: vocab).Terms.Single().Fuzzy);
        Assert.Equal(0, vocab.NearestCalls);                                           // the word list was not even scanned
    }

    [Theory]
    [InlineData("pap")]                 // too short
    [InlineData("\"papzoglou\"")]       // a phrase
    public void ShortWordsAndPhrases_AreNotCorrected(string text)
    {
        var vocab = new FakeVocabulary("papazoglou", "pop");

        Assert.Empty(Expand(text, vocabulary: vocab).Terms.Single().Fuzzy);
    }

    [Fact]
    public void WithTypoMatchingOff_ThereAreNoCandidates()
    {
        var vocab = new FakeVocabulary("papazoglou");

        Assert.Empty(Expand("papzoglou", new ExpansionOptions { Fuzzy = false }, vocab).Terms.Single().Fuzzy);
        Assert.Equal(0, vocab.NearestCalls);
    }

    [Fact]
    public void WithoutAVocabulary_ThereAreNoCandidates() => Assert.Empty(Expand("papzoglou").Terms.Single().Fuzzy);

    // ===== caps =====

    [Fact]
    public void AManyWordQuery_StaysWithinTheTotalCap_AndKeepsEveryNormalizedWord()
    {
        var text = string.Join(' ', Enumerable.Range(0, 30).Select(i => "xaris" + (char)('a' + i % 20)));

        var q = Expand(text);

        Assert.True(q.TotalForms <= 64, $"{q.TotalForms} forms");
        Assert.Equal(30, q.Terms.Count);
        Assert.All(q.Terms, t => Assert.False(string.IsNullOrEmpty(t.N)));
    }

    [Fact]
    public void WhenTrimming_TheWeakestFormsGoFirst_AndTheLastWordsFirst()
    {
        var vocab = new FakeVocabulary("papazoglou", "papazoglu");
        // Ten identical typos, each with a candidate. Allow only 3 forms fewer than they need: only the LAST words give up their candidates.
        var text = string.Join(' ', Enumerable.Repeat("papzoglou", 10));
        var full = Expand(text, new ExpansionOptions { MaxTotal = 1000 }, vocab);
        var cap = full.TotalForms - 3;

        var q = Expand(text, new ExpansionOptions { MaxTotal = cap }, vocab);

        Assert.True(q.TotalForms <= cap);
        Assert.NotEmpty(q.Terms[0].Fuzzy);                        // the first words keep their candidates...
        Assert.Empty(q.Terms[^1].Fuzzy);                          // ...the last ones lose them first
        Assert.All(q.Terms, t => Assert.Equal("papzoglou", t.N));
    }

    [Fact]
    public void TheCapIsDeterministic()
    {
        var text = string.Join(' ', Enumerable.Range(0, 30).Select(i => "kalhmera" + i));

        Assert.Equal(Expand(text), Expand(text), new ExpandedQueryComparer());
    }

    private sealed class ExpandedQueryComparer : IEqualityComparer<ExpandedQuery>
    {
        public bool Equals(ExpandedQuery? a, ExpandedQuery? b) =>
            a is not null && b is not null && a.Terms.Count == b.Terms.Count &&
            a.Terms.Zip(b.Terms).All(p => p.First.N == p.Second.N && p.First.KB == p.Second.KB && p.First.K == p.Second.K && p.First.KBK == p.Second.KBK);
        public int GetHashCode(ExpandedQuery q) => q.Terms.Count;
    }
}

public class FtsMatchBuilderTests
{
    private static FtsMatchSet Build(string text, ExpansionOptions? options = null) =>
        FtsMatchBuilder.Build(QueryExpander.Expand(SearchQueryParser.Parse(text), options ?? new ExpansionOptions()));

    [Fact]
    public void NothingSearchable_GivesAnEmptySet()
    {
        Assert.True(Build("").IsEmpty);
        Assert.True(Build("!!!").IsEmpty);
        Assert.Null(Build("...").All);
    }

    [Fact]
    public void AWord_IsLookedUpAsTypedThenNormalizedThenByKeyboardAndKey()
    {
        var m = Build("xaris");

        Assert.Equal("\"xaris\"*", m.Exact);
        Assert.Equal("{TitleN ArtistN AlbumN GenreN FileN Extra}:\"xaris\"*", m.Tier1);
        Assert.Contains("{TitleN ArtistN AlbumN GenreN FileN Extra}:\"χαρισ\"*", m.Tier2);
        Assert.Contains("{TitleK ArtistK AlbumK GenreK FileK Extra}:\"xaris\"*", m.Tier3);
        Assert.Equal(m.Tier3, m.All);                                  // no typo candidates without a vocabulary
    }

    [Fact]
    public void Tiers_AreCumulative()
    {
        var m = Build("xaris");

        Assert.Contains(m.Tier1!, m.Tier2!);
        Assert.Contains("OR", m.Tier2!);
    }

    [Fact]
    public void IdenticalTiers_AreNotSearchedTwice()
    {
        // "1984" has no keyboard reading, so tier 2 equals tier 1; the key adds nothing in the N columns but does exist in the K columns.
        var m = Build("1984");

        Assert.Null(m.Tier2);
        Assert.NotNull(m.Tier3);
    }

    [Fact]
    public void WithSmartGreekOff_TheAliasTiersHaveNoExtraForms()
    {
        var m = Build("xaris", new ExpansionOptions { SmartGreek = false });

        Assert.Null(m.Tier2);
        Assert.Null(m.Tier3);
        Assert.Equal(m.Tier1, m.All);
    }

    [Fact]
    public void EveryTermMustMatch()
    {
        var m = Build("xaris alexiou");

        Assert.Equal("\"xaris\"* AND \"alexiou\"*", m.Exact);
        Assert.Contains(" AND ", m.Tier1!);
        Assert.Equal(2, m.Tier1!.Split(" AND ").Length);
    }

    [Fact]
    public void AQuoteInTheQuery_CannotEscapeTheLiteral()
    {
        var m = Build("a\"b\" OR NEAR(x) *");

        foreach (var s in new[] { m.Exact, m.Legacy, m.Tier1, m.Tier2, m.Tier3, m.All, m.Primary })
        {
            if (s is null) continue;
            // Every double quote is either a delimiter or doubled, and no bare operator appears outside a quoted string.
            Assert.Equal(0, UnquotedOperators(s));
        }
    }

    [Theory]
    [InlineData("OR")]
    [InlineData("AND")]
    [InlineData("NOT")]
    [InlineData("NEAR(")]
    [InlineData("a:b")]
    [InlineData("*")]
    [InlineData("^x")]
    [InlineData("-x")]
    [InlineData("x*y")]
    [InlineData("(((")]
    public void FtsSyntaxInTheQuery_StaysLiteral(string text)
    {
        var m = Build(text);
        foreach (var s in new[] { m.Exact, m.Legacy, m.Tier1, m.Tier2, m.Tier3, m.All, m.Primary })
            if (s is not null) Assert.Equal(0, UnquotedOperators(s));
    }

    /// <summary>Counts FTS operator words that appear outside double-quoted strings and outside our own AND/OR glue.</summary>
    private static int UnquotedOperators(string match)
    {
        var inQuote = false;
        var outside = new System.Text.StringBuilder();
        for (var i = 0; i < match.Length; i++)
        {
            if (match[i] == '"')
            {
                if (inQuote && i + 1 < match.Length && match[i + 1] == '"') { i++; continue; } // doubled quote inside a literal
                inQuote = !inQuote;
                outside.Append('"');
                continue;
            }
            if (!inQuote) outside.Append(match[i]);
        }
        var text = outside.ToString();
        // What may remain outside literals: our column filters, parentheses, stars, AND / OR glue and whitespace.
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\{[A-Za-z ]+\}:|[A-Za-z]+:|\(|\)|\*|\s|\bAND\b|\bOR\b|""+", "");
        return text.Length;
    }

    [Fact]
    public void AFieldFilter_UsesOnlyThatFieldsColumns()
    {
        var m = Build("artist:xaris");

        Assert.Equal("Artist:\"xaris\"*", m.Exact);
        Assert.StartsWith("{ArtistN Extra}:", m.Tier1);
        Assert.DoesNotContain("TitleN", m.Tier1);
        Assert.Contains("{ArtistK Extra}:", m.Tier3);
    }

    [Fact]
    public void AGenreFilter_IsNeverAPrimaryMatch()
    {
        Assert.Null(Build("genre:disco").Primary);
        Assert.Null(Build("genre:disco").ExactPrimary);
        Assert.NotNull(Build("title:disco").Primary);
    }

    [Fact]
    public void APlainWord_HasAPrimaryExpression_OnTitleArtistAndAlbum()
    {
        var m = Build("xaris");

        Assert.StartsWith("(", m.Primary);
        Assert.Contains("{TitleN ArtistN AlbumN Extra}", m.Primary);
        Assert.DoesNotContain("FileN", m.Primary);                     // a match only in the path is not "primary"
        Assert.Equal("{Title Artist AlbumArtist Album}:\"xaris\"*", m.ExactPrimary);
    }

    [Fact]
    public void APhrase_HasNoPrefixStar_OnItsWholeWords()
    {
        var m = Build("\"le freak\"");

        Assert.Equal("\"le freak\"", m.Exact);
        Assert.Equal("{TitleN ArtistN AlbumN GenreN FileN Extra}:\"le freak\"", m.Tier1);
    }

    [Fact]
    public void AShortKey_IsMatchedAsAWholeWord_ALongOneAsAPrefix()
    {
        // "th" has the key "th" (2 characters): as a prefix it would pull in every word starting with θ.
        Assert.Contains("{TitleK ArtistK AlbumK GenreK FileK Extra}:\"th\"", Build("th").Tier3);
        Assert.DoesNotContain("\"th\"*", Build("th").Tier3!.Split(" OR ").Last());
        Assert.Contains("\"alex\"*", Build("alex").Tier3);
    }

    [Fact]
    public void Legacy_KeepsThePreM7Expression()
    {
        Assert.Equal("(\"Παπάζογλου\"* OR NormalizedText:\"παπαζογλου\"*)", Build("Παπάζογλου").Legacy);
        Assert.Equal("Artist:\"x\"*", Build("artist:x").Legacy);
    }
}
