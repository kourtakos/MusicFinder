using Dapper;
using MusicFinder.Application.Search;
using MusicFinder.Infrastructure.Persistence;

namespace MusicFinder.Infrastructure.Search;

/// <summary>
/// The distinct words that exist in the library (normalized title, artist, album, genre and file words), loaded once from the alias index
/// and kept in memory, so typos can be corrected against real words. Searching never computes an edit distance over tracks: only over
/// a small band of this sorted word list, and only when nothing in the library starts with what was typed.
/// </summary>
public sealed class FtsVocabulary : IVocabularyLookup
{
    /// <summary>A band scan never looks at more than this many words, so a typo lookup stays a few milliseconds even for a huge library.</summary>
    private const int MaxScan = 5000;

    private readonly SqliteConnectionFactory _factory;
    private readonly object _gate = new();
    private (string Term, int Docs)[] _words = [];
    private bool _loaded;
    private int _generation;

    private readonly Microsoft.Extensions.Logging.ILogger _log;

    public FtsVocabulary(SqliteConnectionFactory factory, Microsoft.Extensions.Logging.ILogger<FtsVocabulary>? log = null)
    {
        _factory = factory;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<FtsVocabulary>.Instance;
    }

    /// <summary>True once the word list has been read. Until then no typo correction is offered (the safe default).</summary>
    public bool IsLoaded => Volatile.Read(ref _loaded);

    public int WordCount => _words.Length;

    /// <summary>The index changed: the next <see cref="EnsureLoadedAsync"/> rereads it.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _generation++;
            _loaded = false;
        }
    }

    /// <summary>Rereads the word list without blocking the caller. A failure leaves typo correction off until the next change; search itself is unaffected.</summary>
    public void ReloadInBackground()
    {
        Invalidate();
        _ = Task.Run(async () =>
        {
            try { await EnsureLoadedAsync(); }
            catch (Exception ex) { Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(_log, ex, "Could not load the search vocabulary"); }
        });
    }

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        int generation;
        lock (_gate)
        {
            if (_loaded) return;
            generation = _generation;
        }

        using var conn = _factory.Open();
        var rows = await conn.QueryAsync<(string Term, int Docs)>(new CommandDefinition(
            @"SELECT term, SUM(doc) FROM TrackAliasVocab
              WHERE col IN ('TitleN', 'ArtistN', 'AlbumN', 'GenreN', 'FileN') GROUP BY term", cancellationToken: ct));
        var words = rows.OrderBy(r => r.Term, StringComparer.Ordinal).ToArray();

        lock (_gate)
        {
            if (generation != _generation) return; // the index changed again while reading; the next call loads the newer one
            _words = words;
            _loaded = true;
        }
    }

    public bool HasPrefix(string normalizedPrefix)
    {
        if (!IsLoaded) return true; // unknown yet: behave as if the word is fine, so nothing is "corrected" from an empty list
        var words = _words;
        var i = LowerBound(words, normalizedPrefix);
        return i < words.Length && words[i].Term.StartsWith(normalizedPrefix, StringComparison.Ordinal);
    }

    public IReadOnlyList<string> Nearest(string normalizedWord, int max)
    {
        if (!IsLoaded || normalizedWord.Length == 0 || max <= 0) return [];
        var words = _words;
        // One mistake for ordinary words; two only for long words (9+ letters), where a second slip is still unmistakable.
        // The real-library check showed why: "kalhmera" (a Greeklish greeting) must not turn into the track "Kamera".
        var allowed = normalizedWord.Length >= 9 ? 2 : 1;

        // Words of nearly the same length that share the first letter: a typo in the very first letter is not corrected (it is
        // rare, and it is what keeps the scan to one small band of the sorted list).
        var first = normalizedWord[0].ToString();
        var start = LowerBound(words, first);
        var found = new List<(string Term, int Distance, int Docs)>();
        var scanned = 0;
        for (var i = start; i < words.Length && words[i].Term[0] == normalizedWord[0] && scanned < MaxScan; i++, scanned++)
        {
            var term = words[i].Term;
            if (Math.Abs(term.Length - normalizedWord.Length) > allowed || term == normalizedWord) continue;
            var d = EditDistance(normalizedWord, term, allowed);
            if (d <= allowed) found.Add((term, d, words[i].Docs));
        }

        return found
            .OrderBy(f => f.Distance).ThenByDescending(f => f.Docs).ThenBy(f => f.Term, StringComparer.Ordinal)
            .Take(max).Select(f => f.Term).ToList();
    }

    private static int LowerBound((string Term, int Docs)[] words, string value)
    {
        int lo = 0, hi = words.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (string.CompareOrdinal(words[mid].Term, value) < 0) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>
    /// Optimal-string-alignment distance (insert, delete, substitute, swap two neighbours), giving up once it exceeds
    /// <paramref name="limit"/>. A swapped pair of letters counts as one mistake, as it is the most common typo.
    /// </summary>
    public static int EditDistance(string a, string b, int limit)
    {
        if (Math.Abs(a.Length - b.Length) > limit) return limit + 1;

        var previous2 = new int[b.Length + 1];
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowMin = current[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var value = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    value = Math.Min(value, previous2[j - 2] + 1);
                current[j] = value;
                rowMin = Math.Min(rowMin, value);
            }
            if (rowMin > limit) return limit + 1;
            (previous2, previous, current) = (previous, current, previous2);
        }
        return previous[b.Length];
    }
}
