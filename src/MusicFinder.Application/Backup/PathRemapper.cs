using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Backup;

/// <summary>
/// Rewrites the start of a path when a library moved to another drive or share (<c>D:\DJ Music</c> to <c>E:\DJ Music</c>, or a UNC share).
/// Matching works on whole folder names, ignores case, and the longest matching prefix is applied once, so
/// <c>D:\DJ Music</c> never matches <c>D:\DJ Music2</c> and two drives can be swapped.
/// </summary>
public sealed class PathRemapper
{
    private sealed record Rule(string[] OldSegments, string New);

    private readonly List<Rule> _rules;

    public PathRemapper(IEnumerable<PathMapping> mappings)
    {
        _rules = mappings
            .Where(m => !string.IsNullOrWhiteSpace(m.OldPrefix) && !string.IsNullOrWhiteSpace(m.NewPrefix))
            .Select(m => new Rule(Segments(Normalize(m.OldPrefix)), Normalize(m.NewPrefix)))
            .OrderByDescending(r => r.OldSegments.Length)
            .ToList();
    }

    public bool IsEmpty => _rules.Count == 0;

    /// <summary>The path with its prefix replaced, or the path unchanged when no mapping applies. <paramref name="wasPrefixItself"/> is true when the path was exactly a mapped folder.</summary>
    public string Apply(string path, out bool wasPrefixItself)
    {
        wasPrefixItself = false;
        var segments = Segments(path.Replace('/', '\\'));
        foreach (var rule in _rules)
        {
            if (segments.Length < rule.OldSegments.Length) continue;
            var match = true;
            for (var i = 0; i < rule.OldSegments.Length && match; i++)
                match = string.Equals(segments[i], rule.OldSegments[i], StringComparison.OrdinalIgnoreCase);
            if (!match) continue;

            var rest = segments[rule.OldSegments.Length..];
            wasPrefixItself = rest.Length == 0;
            return rest.Length == 0 ? rule.New : rule.New.TrimEnd('\\') + "\\" + string.Join("\\", rest);
        }
        return path;
    }

    public string Apply(string path) => Apply(path, out _);

    /// <summary>Paths that would end up identical (ignoring case) after the mapping. A mapping that merges two folders must be refused.</summary>
    public static IReadOnlyList<string> FindCollisions(IEnumerable<string> mappedPaths) =>
        mappedPaths.GroupBy(p => p, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

    /// <summary>Forward slashes to backslashes; <c>\\?\</c> and <c>\\?\UNC\</c> prefixes removed; no trailing separator except on a drive root.</summary>
    public static string Normalize(string prefix)
    {
        var p = prefix.Trim().Replace('/', '\\');
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) p = @"\\" + p[8..];
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p[4..];
        var trimmed = p.TrimEnd('\\');
        if (trimmed.Length == 2 && trimmed[1] == ':') return trimmed + "\\";           // drive root keeps its separator
        return trimmed;
    }

    private static string[] Segments(string normalized) =>
        normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
}
