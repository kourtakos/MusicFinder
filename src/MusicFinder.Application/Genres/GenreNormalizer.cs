using System.Text.RegularExpressions;
using MusicFinder.Application.Services;
using MusicFinder.Domain.Entities;

namespace MusicFinder.Application.Genres;

public sealed record GenreMapResult(IReadOnlyList<long> GenreIds, IReadOnlyList<string> Unmapped);

/// <summary>
/// Maps a raw embedded genre string ("Disco;Funk", "Nu-Disco", "Λαϊκά") onto taxonomy genres using exact,
/// normalized matches of genre names and aliases. No fuzzy or substring matching: "Rock" must never claim
/// "Rockabilly". Anything unmatched is reported so the user can map it with one click.
/// </summary>
public sealed partial class GenreNormalizer
{
    private static readonly char[] Separators = [';', '/', ',', '\\', '|'];
    private readonly Dictionary<string, long> _lookup = new(StringComparer.Ordinal);

    public GenreNormalizer(IEnumerable<Genre> genres, IEnumerable<GenreAlias> aliases)
    {
        foreach (var g in genres) _lookup[g.NormalizedName] = g.Id;
        foreach (var a in aliases) _lookup.TryAdd(a.AliasNormalized, a.GenreId); // a genre name always wins over an alias
    }

    public GenreMapResult Map(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new GenreMapResult([], []);

        var ids = new List<long>();
        var unmapped = new List<string>();
        foreach (var token in raw.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryResolve(token, out var id))
            {
                if (!ids.Contains(id)) ids.Add(id);
            }
            else if (!unmapped.Contains(token, StringComparer.OrdinalIgnoreCase))
            {
                unmapped.Add(token);
            }
        }
        return new GenreMapResult(ids, unmapped);
    }

    private bool TryResolve(string token, out long id)
    {
        id = 0;
        var key = TextNormalizer.Normalize(token);
        if (key.Length == 0) return false;
        if (_lookup.TryGetValue(key, out id)) return true;

        // "Rock (Live)" / "House [Remix]": retry once without a trailing bracketed qualifier.
        var stripped = Qualifier().Replace(token, "").Trim();
        if (stripped.Length > 0 && stripped != token)
        {
            var strippedKey = TextNormalizer.Normalize(stripped);
            if (strippedKey.Length > 0 && _lookup.TryGetValue(strippedKey, out id)) return true;
        }
        return false;
    }

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*[\)\]]\s*$")]
    private static partial Regex Qualifier();
}
