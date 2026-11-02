using System.Text;

namespace MusicFinder.Application.Search;

/// <summary>
/// One coarse Latin "sound key" for Greek and for Greeklish/English text, so that <c>Χάρις</c>, <c>xaris</c>, <c>haris</c> and <c>charis</c>
/// all become <c>xaris</c>. Instead of enumerating every way a Greek word can be spelled in Latin letters (there are dozens),
/// both sides are reduced to the same key and the ambiguity disappears: sounds that people spell differently are merged.
/// </summary>
/// <remarks>
/// This is heuristic by nature. Greeklish has no one-to-one mapping, and the merges (χ and ξ both become <c>x</c>, η/ι/υ/ει/οι all become <c>i</c>)
/// mean that different words can share a key. The key is therefore used only in the lower relevance tiers, after exact and accent-insensitive matches.
/// The same function runs when a track is indexed and when a query is typed, so the two always agree. Input is expected to be already
/// normalized (<see cref="Services.TextNormalizer"/>: lowercase, no accents, σ only); other characters pass through unchanged.
/// </remarks>
public static class GreekPhoneticKey
{
    private static readonly (string From, string To)[] GreekDigraphs =
    [
        ("ου", "u"), ("αι", "e"), ("ει", "i"), ("οι", "i"), ("υι", "i"), ("αυ", "av"), ("ευ", "ev"), ("ηυ", "iv"),
        ("μπ", "b"), ("ντ", "d"), ("γκ", "g"), ("γγ", "g"), ("τσ", "ts"), ("τζ", "tz")
    ];

    private static readonly Dictionary<char, string> GreekLetters = new()
    {
        ['α'] = "a", ['β'] = "v", ['γ'] = "g", ['δ'] = "d", ['ε'] = "e", ['ζ'] = "z", ['η'] = "i", ['θ'] = "th", ['ι'] = "i",
        ['κ'] = "k", ['λ'] = "l", ['μ'] = "m", ['ν'] = "n", ['ξ'] = "x", ['ο'] = "o", ['π'] = "p", ['ρ'] = "r", ['σ'] = "s",
        ['ς'] = "s", ['τ'] = "t", ['υ'] = "i", ['φ'] = "f", ['χ'] = "x", ['ψ'] = "ps", ['ω'] = "o"
    };

    private static readonly (string From, string To)[] LatinDigraphs =
    [
        ("ch", "x"), ("th", "th"), ("ph", "f"), ("ps", "ps"), ("ts", "ts"), ("tz", "tz"), ("ks", "x"),
        ("ou", "u"), ("oy", "u"), ("ai", "e"), ("ei", "i"), ("oi", "i"), ("yi", "i"),
        ("au", "av"), ("af", "av"), ("eu", "ev"), ("ef", "ev"),
        ("mp", "b"), ("nt", "d"), ("gk", "g"), ("gg", "g")
    ];

    private const string LatinVowels = "aeiouy";

    /// <summary>The key of one normalized word. Empty for an empty word.</summary>
    public static string Key(string word)
    {
        if (word.Length == 0) return "";
        var sb = new StringBuilder(word.Length + 4);
        var i = 0;
        while (i < word.Length)
        {
            if (TryDigraph(word, i, out var piece, out var length)) { sb.Append(piece); i += length; continue; }

            var c = word[i];
            if (GreekLetters.TryGetValue(c, out var greek)) sb.Append(greek);
            else sb.Append(LatinLetter(word, i));
            i++;
        }
        return Collapse(sb);
    }

    /// <summary>The key of a whole normalized text: each word keyed, joined by single spaces.</summary>
    public static string KeyText(string normalizedText)
    {
        if (normalizedText.Length == 0) return "";
        var words = normalizedText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(Key).Where(k => k.Length > 0));
    }

    private static bool TryDigraph(string word, int i, out string piece, out int length)
    {
        piece = "";
        length = 2;
        if (i + 1 >= word.Length) return false;
        var pair = word.AsSpan(i, 2);
        foreach (var (from, to) in GreekDigraphs)
            if (pair.SequenceEqual(from)) { piece = to; return true; }
        foreach (var (from, to) in LatinDigraphs)
            if (pair.SequenceEqual(from)) { piece = to; return true; }
        return false;
    }

    /// <summary>A single Latin letter. "y" and "h" depend on their neighbours: yorgos is γι-, kalhmera is -ή-, haris is χ-.</summary>
    private static string LatinLetter(string word, int i)
    {
        var c = word[i];
        switch (c)
        {
            case 'y':
                // At the start of a word before a vowel it is the sound of γι (yorgos, yannis); elsewhere it is υ.
                return i == 0 && i + 1 < word.Length && LatinVowels.Contains(word[i + 1]) ? "gi" : "i";

            case 'h':
                // Between two consonants, or after a consonant at the end of a word, it is η (kalhmera, mazwnakhs); otherwise χ (haris, ahinos).
                var before = i > 0 && IsLatinConsonant(word[i - 1]);
                var after = i + 1 >= word.Length || IsLatinConsonant(word[i + 1]);
                return before && after ? "i" : "x";

            case 'w': return "o";
            case 'u': return "i";
            case 'c': case 'q': return "k";
            case 'j': return "tz";
            default: return c.ToString();
        }
    }

    private static bool IsLatinConsonant(char c) => c is >= 'a' and <= 'z' && !LatinVowels.Contains(c);

    private static string Collapse(StringBuilder sb)
    {
        var result = new StringBuilder(sb.Length);
        foreach (var c in sb.ToString())
            if (result.Length == 0 || result[^1] != c) result.Append(c);
        return result.ToString();
    }
}
