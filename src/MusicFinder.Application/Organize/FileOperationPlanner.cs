using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Organize;

/// <summary>Asks whether a path exists right now. A file is never opened or changed by the planner.</summary>
public interface IFileProbe
{
    bool Exists(string path);
}

/// <summary>
/// Works out, without touching anything, what a rename would do: the new name, why a file is skipped, and every collision
/// (with a file on disk, inside the batch, or with a sidecar). The result is exactly what the user previews and confirms.
/// </summary>
public static class FileOperationPlanner
{
    private static readonly string[] SidecarExtensions = [".lrc", ".cue"];

    public static IReadOnlyList<PlanItem> PlanRename(IEnumerable<RenameSource> sources, RenameOptions options, IFileProbe probe)
    {
        var template = FileNameTemplate.Parse(options.Template);
        var items = new List<PlanItem>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var list = sources.ToList();
        if (template.Error is not null)
            return list.Select(s => Skip(s, template.Error)).ToList();

        // Files that keep their name still occupy it; claim those first so a rename cannot land on them.
        var planned = new List<(RenameSource Src, string? NewPath, string? Reason)>();
        foreach (var s in list)
        {
            var (path, reason) = NewPathFor(s, template, options);
            planned.Add((s, path, reason));
            if (path is not null && string.Equals(path, s.FullPath, StringComparison.Ordinal)) claimed.Add(path);
        }

        foreach (var (src, newPath, reason) in planned)
        {
            if (newPath is null) { items.Add(Skip(src, reason!)); continue; }

            if (!probe.Exists(src.FullPath)) { items.Add(Skip(src, "The file was not found (is the drive offline, or was it moved?).")); continue; }

            if (string.Equals(newPath, src.FullPath, StringComparison.Ordinal))
            {
                items.Add(new PlanItem(src.TrackId, src.FullPath, newPath, PlanStatus.Unchanged, "Already has this name.", []));
                continue;
            }

            var caseOnly = string.Equals(newPath, src.FullPath, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly)
            {
                if (probe.Exists(newPath)) { items.Add(Skip(src, "A file with the new name already exists.")); continue; }
                if (claimed.Contains(newPath)) { items.Add(Skip(src, "Another file in this batch gets the same new name.")); continue; }
            }

            var sidecars = new List<PlannedSidecar>();
            var blocked = (string?)null;
            if (options.IncludeSidecars)
            {
                var oldStem = Path.Combine(Path.GetDirectoryName(src.FullPath)!, Path.GetFileNameWithoutExtension(src.FullPath));
                var newStem = Path.Combine(Path.GetDirectoryName(newPath)!, Path.GetFileNameWithoutExtension(newPath));
                foreach (var ext in SidecarExtensions)
                {
                    var from = oldStem + ext;
                    if (!probe.Exists(from)) continue;
                    var to = newStem + ext;
                    var sameFile = string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
                    if (!sameFile && (probe.Exists(to) || claimed.Contains(to))) { blocked = $"The {ext} file beside it would collide with an existing file."; break; }
                    sidecars.Add(new PlannedSidecar(from, to));
                }
            }
            if (blocked is not null) { items.Add(Skip(src, blocked)); continue; }

            claimed.Add(newPath);
            foreach (var sc in sidecars) claimed.Add(sc.NewPath);
            items.Add(new PlanItem(src.TrackId, src.FullPath, newPath, caseOnly ? PlanStatus.CaseOnly : PlanStatus.Rename, null, sidecars));
        }
        return items;
    }

    private static (string? Path, string? Reason) NewPathFor(RenameSource s, FileNameTemplate template, RenameOptions options)
    {
        var rendered = template.Render(s, options.UsePlaceholders);
        if (rendered.Text is null)
            return (null, $"No value for {string.Join(", ", rendered.Missing.Select(m => "{" + m + "}"))}.");

        var name = rendered.Text;
        if (!template.HasExtensionToken)
        {
            var ext = Path.GetExtension(s.FullPath);
            if (!name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) name += ext;
        }
        name = PathSanitizer.Segment(name);

        var dir = Path.GetDirectoryName(s.FullPath);
        if (string.IsNullOrEmpty(dir)) return (null, "The file has no folder.");
        var full = Path.Combine(dir, name);
        if (full.Length > PathSanitizer.MaxPathLength) return (null, $"The new path would be {full.Length} characters long (the limit is {PathSanitizer.MaxPathLength}).");
        return (full, null);
    }

    private static PlanItem Skip(RenameSource s, string reason) =>
        new(s.TrackId, s.FullPath, s.FullPath, PlanStatus.Skipped, reason, []);
}
