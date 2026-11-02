using MusicFinder.Application.Services;

namespace MusicFinder.Application.Search;

/// <summary>Typo candidates drawn from the words that really exist in the library (never from scanning tracks).</summary>
public interface IVocabularyLookup
{
    /// <summary>True when some indexed word starts with this normalized text (the user is simply still typing it).</summary>
    bool HasPrefix(string normalizedPrefix);

    /// <summary>Indexed words close to this one, best first.</summary>
    IReadOnlyList<string> Nearest(string normalizedWord, int max);
}

public sealed record ExpansionOptions
{
    public bool SmartGreek { get; init; } = true;
    public bool Fuzzy { get; init; } = true;
    public int MaxPerToken { get; init; } = 12;
    public int MaxTotal { get; init; } = 64;
}

/// <summary>
/// One search word or phrase with every form it may be found under, strongest first:
/// <see cref="Raw"/> as typed, <see cref="N"/> normalized, <see cref="KB"/> if the keyboard layout was wrong,
/// <see cref="K"/> / <see cref="KBK"/> the phonetic keys (Greeklish), <see cref="Fuzzy"/> close real words (typos).
/// </summary>
public sealed record ExpandedTerm(
    string? Field, bool IsPhrase, string Raw, string N, string? KB, string? K, string? KBK, IReadOnlyList<string> Fuzzy)
{
    public int FormCount => 1 + (KB is null ? 0 : 1) + (K is null ? 0 : 1) + (KBK is null ? 0 : 1) + Fuzzy.Count;
}

public sealed record ExpandedQuery(IReadOnlyList<ExpandedTerm> Terms)
{
    public bool IsEmpty => Terms.Count == 0;
    public int TotalForms => Terms.Sum(t => t.FormCount);
}

/// <summary>
/// Turns what the user typed into the bounded set of forms smart search looks for. Pure: the optional vocabulary is the only outside input.
/// Every form is later quoted as a literal, so none of this can inject FTS syntax.
/// </summary>
public static class QueryExpander
{
    private const int MaxFuzzy = 3;
    private const int MinFuzzyLength = 4;

    public static ExpandedQuery Expand(ParsedQuery parsed, ExpansionOptions options, IVocabularyLookup? vocabulary = null)
    {
        var terms = new List<ExpandedTerm>();
        foreach (var term in parsed.Terms)
        {
            var n = TextNormalizer.Normalize(term.Value);
            if (n.Length == 0) continue; // punctuation-only input has nothing to match
            terms.Add(options.SmartGreek ? ExpandSmart(term, n, options, vocabulary) : new ExpandedTerm(term.Field, term.IsPhrase, term.Value, n, null, null, null, []));
        }
        return new ExpandedQuery(Cap(terms, options));
    }

    private static ExpandedTerm ExpandSmart(TextTerm term, string n, ExpansionOptions options, IVocabularyLookup? vocabulary)
    {
        // The key is searched even when it equals the normalized text: the K columns hold the keys of Greek titles, so "kalimera" must be looked up there to find Καλημέρα.
        var keyText = GreekPhoneticKey.KeyText(n);
        var k = keyText.Length == 0 ? null : keyText;

        // Wrong keyboard layout: the same physical keys read in the other alphabet.
        string? kb = null;
        string? kbk = null;
        var mapped = MapLayout(term.Value);
        if (mapped is not null)
        {
            var normalizedMapped = TextNormalizer.Normalize(mapped);
            if (normalizedMapped.Length > 0 && normalizedMapped != n)
            {
                kb = normalizedMapped;
                kbk = Distinct(GreekPhoneticKey.KeyText(kb), k);
            }
        }

        // Typos: only when nothing in the library starts with what was typed, and only for a single word.
        IReadOnlyList<string> fuzzy = [];
        if (options.Fuzzy && vocabulary is not null && !term.IsPhrase && n.Length >= MinFuzzyLength && !n.Contains(' ') && !vocabulary.HasPrefix(n))
            fuzzy = vocabulary.Nearest(n, MaxFuzzy).Where(f => f != n && f != kb).Take(MaxFuzzy).ToList();

        return new ExpandedTerm(term.Field, term.IsPhrase, term.Value, n, kb, k, kbk, fuzzy);
    }

    /// <summary>The other-layout reading of each word, or null when no word has a plausible one.</summary>
    private static string? MapLayout(string raw)
    {
        var any = false;
        var words = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w =>
        {
            if (GreekKeyboardLayout.TryMapToGreek(w, out var greek)) { any = true; return greek; }
            if (GreekKeyboardLayout.TryMapToLatin(w, out var latin)) { any = true; return latin; }
            return w;
        }).ToList();
        return any ? string.Join(' ', words) : null;
    }

    /// <summary>A key equal to one already searched in the same columns is dropped, so it is not searched twice.</summary>
    private static string? Distinct(string candidate, params string?[] existing) =>
        candidate.Length == 0 || existing.Contains(candidate) ? null : candidate;

    /// <summary>
    /// Keeps the whole query within <see cref="ExpansionOptions.MaxTotal"/> forms by dropping the weakest kinds first, last term first:
    /// typo candidates, then keyboard keys, then keyboard variants, then phonetic keys. The normalized form of every term always stays.
    /// </summary>
    private static List<ExpandedTerm> Cap(List<ExpandedTerm> terms, ExpansionOptions options)
    {
        terms = terms.Select(t => t.FormCount <= options.MaxPerToken ? t : t with { Fuzzy = t.Fuzzy.Take(Math.Max(0, t.Fuzzy.Count - (t.FormCount - options.MaxPerToken))).ToList() }).ToList();

        var stages = new Func<ExpandedTerm, ExpandedTerm>[]
        {
            t => t with { Fuzzy = [] },
            t => t with { KBK = null },
            t => t with { KB = null },
            t => t with { K = null }
        };
        foreach (var stage in stages)
        {
            for (var i = terms.Count - 1; i >= 0 && terms.Sum(t => t.FormCount) > options.MaxTotal; i--)
                terms[i] = stage(terms[i]);
        }
        return terms;
    }
}
