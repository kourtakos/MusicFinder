using MusicFinder.Application.Search;
using MusicFinder.Application.Services;

namespace MusicFinder.UnitTests;

public class GreekKeyboardLayoutTests
{
    private static string ToGreek(string raw)
    {
        Assert.True(GreekKeyboardLayout.TryMapToGreek(raw, out var greek), $"'{raw}' should map");
        return greek;
    }

    private static string ToLatin(string raw)
    {
        Assert.True(GreekKeyboardLayout.TryMapToLatin(raw, out var latin), $"'{raw}' should map");
        return latin;
    }

    // Greek text typed with the US layout active. ';' is the tonos dead key, so Χάρις is x ; a r i s on a US keyboard,
    // and ου is o + y (the U key is θ). Compared after normalization, which folds final sigma and accents.
    [Theory]
    [InlineData("x;aris", "χάρις")]
    [InlineData("alej;ioy", "αλεξίου")]
    [InlineData("papazogloy", "παπαζογλου")]
    [InlineData("mazvnak;hs", "μαζωνάκης")]
    [InlineData("gi;vrgos", "γιώργος")]
    [InlineData("maz;vn;akhs", "μαζώνάκης")]
    public void TheUsKeysOfAGreekWord_BecomeTheGreekWord(string typed, string expectedGreek) =>
        Assert.Equal(TextNormalizer.Normalize(expectedGreek), TextNormalizer.Normalize(ToGreek(typed)));

    [Fact]
    public void TheTonosDeadKey_ProducesTheAccentedVowel() => Assert.Equal("χάρισ", ToGreek("x;aris"));

    // The Greek keys of a Latin word (the Greek layout was active). Σ is typed with the S key, ς with W.
    [Theory]
    [InlineData("γιοργοσ", "giorgos")]
    [InlineData("ΓΙΟΡΓΟΣ", "giorgos")]
    [InlineData("ψηιψ", "chic")]
    [InlineData("δαφτ", "daft")]
    [InlineData("ςομε", "wome")]
    public void TheGreekKeysOfALatinWord_BecomeTheLatinWord(string typed, string expectedLatin) => Assert.Equal(expectedLatin, ToLatin(typed));

    [Fact]
    public void Accents_AreIgnoredWhenMappingToLatin()
    {
        Assert.Equal("xaris", ToLatin("χάρισ"));
        Assert.Equal("xariw", ToLatin("χάρις"));   // a final ς is the W key
    }

    [Fact]
    public void LetterKeys_RoundTripBothWays()
    {
        const string us = "wertyuiopasdfghjklzxcvbnm";
        Assert.True(GreekKeyboardLayout.TryMapToGreek(us, out var greek));
        Assert.True(GreekKeyboardLayout.TryMapToLatin(greek, out var back));
        Assert.Equal(us, back);
        Assert.Equal(us.Length, greek.Length);
    }

    [Theory]
    [InlineData("sxt")]            // no Greek vowel in the result
    [InlineData("a")]              // too short
    [InlineData("x")]
    [InlineData("it's")]           // an apostrophe is not a letter key
    [InlineData("abc123")]         // digits
    [InlineData("héllo")]          // already accented Latin: not a keyboard mix-up
    [InlineData("")]
    public void ImplausibleTokens_GetNoLayoutVariant(string raw) => Assert.False(GreekKeyboardLayout.TryMapToGreek(raw, out _));

    [Theory]
    [InlineData("χάρις αλεξίου")]  // a space is not a Greek letter
    [InlineData("abc")]            // Latin: nothing to map back
    [InlineData("χ")]              // too short
    [InlineData("χρ")]             // maps to "xr": no vowel
    [InlineData("mazώ")]           // mixed scripts
    public void ImplausibleGreekTokens_GetNoLayoutVariant(string raw) => Assert.False(GreekKeyboardLayout.TryMapToLatin(raw, out _));

    [Fact]
    public void ADeadKeyThatDoesNotPrecedeAVowel_IsDropped()
    {
        Assert.Equal("τα", ToGreek("ta;"));      // trailing dead key
        Assert.Equal("τα", ToGreek("ta::"));
        Assert.Equal("τρα", ToGreek("t;ra"));    // the tonos only accents the very next vowel; here it meets ρ and is dropped
    }

    [Fact]
    public void TheDialytikaDeadKey_AccentsIotaAndUpsilonOnly()
    {
        Assert.Equal("ϊα", ToGreek(":ia"));
        Assert.Equal("ϋα", ToGreek(":ya"));
        Assert.Equal("εα", ToGreek(":ea"));      // dialytika on a letter that cannot carry it: ignored
    }

    [Fact]
    public void TheGreekQuestionMarkKey_IsPunctuation_NotALetter() => Assert.Equal("ατ", ToGreek("aqt"));

    [Fact]
    public void UppercaseKeys_MapLikeLowercaseOnes() =>
        Assert.Equal(TextNormalizer.Normalize(ToGreek("x;aris")), TextNormalizer.Normalize(ToGreek("X;ARIS")));
}

public class ScriptDetectorTests
{
    [Theory]
    [InlineData("Χάρις", Script.Greek)]
    [InlineData("Charis", Script.Latin)]
    [InlineData("xaris Αλεξίου", Script.Mixed)]
    [InlineData("1984", Script.None)]
    [InlineData("", Script.None)]
    [InlineData(null, Script.None)]
    [InlineData("Мир", Script.Mixed)]          // Cyrillic is not something the Greek rules understand
    [InlineData("café", Script.Mixed)]
    [InlineData("ΣΑΣ", Script.Greek)]
    [InlineData("ϊ-ά", Script.Greek)]
    public void Detects(string? text, Script expected) => Assert.Equal(expected, ScriptDetector.Of(text));
}

public class GreekPhoneticKeyTests
{
    private static string K(string text) => GreekPhoneticKey.KeyText(TextNormalizer.Normalize(text));

    // The fixtures CLAUDE.md section 12E requires, in both directions. Every row must produce ONE key.
    [Theory]
    [InlineData("Παπάζογλου", "papazoglou")]
    [InlineData("Χάρις Αλεξίου", "xaris alexiou")]
    [InlineData("Χάρις Αλεξίου", "haris aleksiou")]
    [InlineData("Χάρις Αλεξίου", "charis alexiou")]
    [InlineData("Αλεξίου", "aleksiou")]
    [InlineData("Μαζωνάκης", "mazonakis")]
    [InlineData("Μαζωνάκης", "mazwnakhs")]
    [InlineData("Γιώργος", "giorgos")]
    [InlineData("Γιώργος", "yorgos")]
    [InlineData("Καλημέρα", "kalhmera")]
    [InlineData("Καλημέρα", "kalimera")]
    [InlineData("Γιάννης", "giannis")]
    [InlineData("Γιάννης", "yannis")]
    [InlineData("Σ' αγαπώ", "s agapo")]
    [InlineData("Μπάμπης", "babis")]
    [InlineData("Ντίνος", "dinos")]
    [InlineData("Ψαραντώνης", "psarantwnhs")]
    [InlineData("Θάνος", "thanos")]
    [InlineData("Χρήστος", "christos")]
    [InlineData("Πάνος Κιάμος", "panos kiamos")]
    public void GreekAndItsGreeklishSpellings_ShareOneKey(string greek, string greeklish) => Assert.Equal(K(greek), K(greeklish));

    [Theory]
    [InlineData("Παπάζογλου", "papazoglu")]
    [InlineData("Χάρις Αλεξίου", "xaris alexiu")]
    [InlineData("Μαζωνάκης", "mazonakis")]
    [InlineData("Γιώργος", "giorgos")]
    [InlineData("Καλημέρα", "kalimera")]
    [InlineData("Γιάννης", "gianis")]            // the doubled ν collapses
    [InlineData("Μπάμπης", "babis")]
    public void TheKeysAreStableAndReadable(string text, string expectedKey) => Assert.Equal(expectedKey, K(text));

    [Theory]
    [InlineData("love", "love")]
    [InlineData("every breath you take", "everi breath giu take")]   // keyed consistently, never compared for equality with Greek
    [InlineData("chic", "xik")]
    public void EnglishText_IsKeyedDeterministically(string english, string key) => Assert.Equal(key, K(english));

    [Theory]
    [InlineData("kalhmera")]
    [InlineData("yorgos")]
    [InlineData("haris")]
    [InlineData("Χάρις")]
    [InlineData("mpampis")]
    public void KeyingTwice_ChangesNothing(string text)
    {
        var once = K(text);
        Assert.Equal(once, GreekPhoneticKey.KeyText(once));
    }

    [Theory]
    [InlineData("haris", "xaris")]           // η/χ before a vowel
    [InlineData("hristos", "xristos")]
    [InlineData("ahinos", "axinos")]
    [InlineData("kalhmera", "kalimera")]     // between consonants: eta
    [InlineData("mazwnakhs", "mazonakis")]   // after a consonant at the end: eta
    [InlineData("thalassa", "thalasa")]
    [InlineData("yorgos", "giorgos")]        // initial y before a vowel
    [InlineData("yannis", "gianis")]
    [InlineData("kyria", "kiria")]           // y elsewhere is ypsilon
    [InlineData("lykos", "likos")]
    public void TheYAndHRules_FollowTheirNeighbours(string word, string key) => Assert.Equal(key, GreekPhoneticKey.Key(word));

    [Theory]
    [InlineData("ou", "u")]
    [InlineData("oy", "u")]
    [InlineData("ai", "e")]
    [InlineData("ei", "i")]
    [InlineData("oi", "i")]
    [InlineData("mpa", "ba")]
    [InlineData("nta", "da")]
    [InlineData("gka", "ga")]
    [InlineData("ggo", "go")]
    [InlineData("tsa", "tsa")]
    [InlineData("tza", "tza")]
    [InlineData("psi", "psi")]
    [InlineData("kso", "xo")]
    [InlineData("xo", "xo")]
    [InlineData("chi", "xi")]
    [InlineData("phi", "fi")]
    [InlineData("auto", "avto")]
    [InlineData("afto", "avto")]
    [InlineData("evaggelia", "evagelia")]
    [InlineData("w", "o")]
    [InlineData("c", "k")]
    public void LatinDigraphsAndLetters_AreReducedToTheKey(string latin, string key) => Assert.Equal(key, GreekPhoneticKey.Key(latin));

    [Theory]
    [InlineData("ου", "u")]
    [InlineData("αι", "e")]
    [InlineData("ει", "i")]
    [InlineData("οι", "i")]
    [InlineData("υι", "i")]
    [InlineData("αυ", "av")]
    [InlineData("ευ", "ev")]
    [InlineData("μπα", "ba")]
    [InlineData("ντα", "da")]
    [InlineData("γκα", "ga")]
    [InlineData("ψι", "psi")]
    [InlineData("θα", "tha")]
    [InlineData("ξο", "xo")]
    [InlineData("χο", "xo")]
    [InlineData("σσ", "s")]
    public void GreekDigraphsAndLetters_AreReducedToTheKey(string greek, string key) => Assert.Equal(key, GreekPhoneticKey.Key(greek));

    [Fact]
    public void EmptyAndOddInput_NeverThrows()
    {
        Assert.Equal("", GreekPhoneticKey.Key(""));
        Assert.Equal("", GreekPhoneticKey.KeyText(""));
        Assert.Equal("123", GreekPhoneticKey.Key("123"));
        Assert.Equal("a b", GreekPhoneticKey.KeyText("a  b"));
        Assert.Equal("é", GreekPhoneticKey.Key("é")); // foreign letters pass through
    }

    [Fact]
    public void DifferentNames_KeepDifferentKeys()
    {
        Assert.NotEqual(K("Παπάζογλου"), K("Μαζωνάκης"));
        Assert.NotEqual(K("Αλεξίου"), K("Αλέκα"));
        Assert.NotEqual(K("chic"), K("chip"));
    }

    [Fact]
    public void KnownHeuristicLimit_DaftPunkIsNotNtaftPankWithoutAnAlias()
    {
        // Greeklish cannot be reversed exactly: "Daft Punk" written in Greek letters differs in vowels, so only a user alias bridges them.
        Assert.NotEqual(K("Daft Punk"), K("νταφτ πανκ"));
    }
}
