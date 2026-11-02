using System.Text.RegularExpressions;
using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Duplicates;

/// <summary>
/// Which copy of byte-identical files is the natural one to keep. It is only a suggestion: nothing acts on it.
/// Identical bytes mean identical audio and tags, so quality cannot tell the copies apart; what can is the history attached
/// to each copy in the library (plays), whether the name looks like a copy ("Song (1).mp3", "Song - Copy.mp3"), and where it sits.
/// </summary>
public static partial class KeepSuggestion
{
    [GeneratedRegex(@"(\(\d+\)|\[\d+\]|[ _-]copy( \d+)?|^copy of )", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CopyLike();

    public static bool LooksLikeACopy(string fullPath) => CopyLike().IsMatch(Path.GetFileNameWithoutExtension(fullPath));

    public static (long TrackId, IReadOnlyList<string> Reasons) Pick(IReadOnlyList<DuplicateMember> members)
    {
        if (members.Count == 0) throw new ArgumentException("A group has at least one member.", nameof(members));

        var ranked = members
            .OrderByDescending(m => m.PlayCount)
            .ThenBy(m => LooksLikeACopy(m.FullPath) ? 1 : 0)
            .ThenBy(m => m.FullPath.Count(c => c is '\\' or '/'))        // the shallower path first
            .ThenBy(m => m.FullPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.TrackId)
            .ToList();
        var keep = ranked[0];
        var others = ranked.Skip(1).ToList();

        var reasons = new List<string>();
        if (others.Count > 0 && keep.PlayCount > others.Max(o => o.PlayCount))
            reasons.Add($"It has the most plays ({keep.PlayCount}); the library's history for the other copies would be left behind");
        if (!LooksLikeACopy(keep.FullPath) && others.Any(o => LooksLikeACopy(o.FullPath)))
            reasons.Add("The others are named like copies");
        if (others.Count > 0 && keep.PlayCount == others.Max(o => o.PlayCount)
            && keep.FullPath.Count(c => c is '\\' or '/') < others.Min(o => o.FullPath.Count(c => c is '\\' or '/')))
            reasons.Add("It is the one nearest the top of the folder tree");
        if (reasons.Count == 0) reasons.Add("The copies are indistinguishable; this one comes first by path");
        return (keep.TrackId, reasons);
    }
}
