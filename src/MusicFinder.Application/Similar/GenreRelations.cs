using MusicFinder.Domain.Entities;

namespace MusicFinder.Application.Similar;

/// <summary>How closely two sets of taxonomy genres are related, from the genre tree (two levels: a family and its sub-genres).</summary>
public sealed class GenreRelations
{
    private readonly Dictionary<long, Genre> _byId;
    private readonly ILookup<long?, Genre> _children;

    public GenreRelations(IEnumerable<Genre> genres)
    {
        var all = genres.ToList();
        _byId = all.ToDictionary(g => g.Id);
        _children = all.ToLookup(g => g.ParentId);
    }

    private long RootOf(long id)
    {
        var seen = 0;
        while (_byId.TryGetValue(id, out var g) && g.ParentId is { } parent && seen++ < 10) id = parent; // depth guard against a corrupt cycle
        return id;
    }

    private string NameOf(long id) => _byId.TryGetValue(id, out var g) ? g.Name : "?";

    /// <summary>
    /// The best relation between any genre of <paramref name="a"/> and any of <paramref name="b"/>:
    /// the same genre 1.0, parent and child or siblings 0.6, only the same family 0.3, otherwise 0. Empty on either side = no information (null).
    /// </summary>
    public (double Value, string? Reason)? Compare(IReadOnlyCollection<long> a, IReadOnlyCollection<long> b)
    {
        if (a.Count == 0 || b.Count == 0) return null;

        (double Value, string? Reason) best = (0, null);
        foreach (var x in a)
        {
            foreach (var y in b)
            {
                var candidate = Relate(x, y);
                if (candidate.Value > best.Value) best = candidate;
                if (best.Value >= 1.0) return best;
            }
        }
        return best;
    }

    private (double Value, string? Reason) Relate(long x, long y)
    {
        if (x == y) return (1.0, $"same genre ({NameOf(x)})");

        _byId.TryGetValue(x, out var gx);
        _byId.TryGetValue(y, out var gy);
        if (gx is null || gy is null) return (0, null);

        var related = gx.ParentId == y || gy.ParentId == x || (gx.ParentId is not null && gx.ParentId == gy.ParentId);
        if (related) return (0.6, $"related genre ({NameOf(x)} ≈ {NameOf(y)})");

        if (RootOf(x) == RootOf(y)) return (0.3, $"same family ({NameOf(RootOf(x))})");
        return (0, null);
    }

    /// <summary>The genres whose tracks can be related to these: themselves, their parents, children and siblings, and the rest of their family.</summary>
    public IReadOnlySet<long> RelatedIds(IEnumerable<long> genreIds)
    {
        var result = new HashSet<long>();
        foreach (var id in genreIds)
        {
            var root = RootOf(id);
            result.Add(root);
            foreach (var child in Descendants(root)) result.Add(child);
        }
        return result;
    }

    private IEnumerable<long> Descendants(long id)
    {
        foreach (var child in _children[id])
        {
            yield return child.Id;
            foreach (var d in Descendants(child.Id)) yield return d;
        }
    }
}
