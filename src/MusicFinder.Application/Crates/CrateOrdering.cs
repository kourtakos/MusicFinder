using MusicFinder.Application.Similar;
using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Crates;

/// <summary>
/// Orders the tracks of a crate. Only observed tags are used: a track with no BPM (or, for the harmonic order, no readable key)
/// is never given a made-up value; it goes after the ordered ones in its current relative order.
/// </summary>
public static class CrateOrdering
{
    /// <summary>Slowest first. Tracks without a BPM follow in their current order.</summary>
    public static IReadOnlyList<long> ByBpm(IReadOnlyList<SearchResultRow> rows)
    {
        var withBpm = rows.Where(r => r.Bpm is > 0).OrderBy(r => r.Bpm).ThenBy(r => r.Id);
        var without = rows.Where(r => r.Bpm is not > 0);
        return withBpm.Concat(without).Select(r => r.Id).ToList();
    }

    /// <summary>
    /// A heuristic running order, not a verdict on what mixes: start with the slowest track that has a BPM and a key, then repeatedly pick
    /// the remaining track that is closest on the Camelot wheel (same key, relative key and neighbours first) and, among those, closest in tempo.
    /// Ties go to the lower track id so the result is the same every time.
    /// </summary>
    public static IReadOnlyList<long> Harmonic(IReadOnlyList<SearchResultRow> rows)
    {
        var usable = rows.Where(r => r.Bpm is > 0 && CamelotKey.TryParse(r.MusicalKey, out _)).ToList();
        var rest = rows.Where(r => !usable.Contains(r)).Select(r => r.Id);
        if (usable.Count == 0) return rows.Select(r => r.Id).ToList();

        var keys = usable.ToDictionary(r => r.Id, r => { CamelotKey.TryParse(r.MusicalKey, out var k); return k; });
        var order = new List<SearchResultRow>(usable.Count);
        var current = usable.OrderBy(r => r.Bpm).ThenBy(r => r.Id).First();
        var left = usable.Where(r => r.Id != current.Id).ToList();
        order.Add(current);
        while (left.Count > 0)
        {
            var from = current;
            current = left
                .OrderBy(r => KeyDistance(keys[from.Id], keys[r.Id]))
                .ThenBy(r => Math.Abs(r.Bpm!.Value - from.Bpm!.Value))
                .ThenBy(r => r.Id)
                .First();
            left.Remove(current);
            order.Add(current);
        }
        return order.Select(r => r.Id).Concat(rest).ToList();
    }

    /// <summary>Steps apart on the Camelot wheel: 0 same key, 1 relative or neighbour, larger is further. The number wraps (12 and 1 are neighbours).</summary>
    internal static int KeyDistance(CamelotKey a, CamelotKey b)
    {
        var step = Math.Abs(a.Number - b.Number);
        return Math.Min(step, 12 - step) + (a.IsMinor == b.IsMinor ? 0 : 1);
    }
}
