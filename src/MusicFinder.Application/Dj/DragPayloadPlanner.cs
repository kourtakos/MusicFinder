namespace MusicFinder.Application.Dj;

/// <summary>A track the user wants to drag. <see cref="KnownMissing"/> comes from the index (the scanner's Missing flag).</summary>
public sealed record DragCandidate(long TrackId, string Path, bool KnownMissing);

/// <param name="Included">What will be put on the drag, in selection order.</param>
/// <param name="SkippedMissing">Files left out because they do not exist; we never offer a path that is not there.</param>
/// <param name="Truncated">How many selected files were beyond the cap and left out.</param>
public sealed record DragPayload(IReadOnlyList<DragCandidate> Included, IReadOnlyList<DragCandidate> SkippedMissing, int Truncated)
{
    public bool IsEmpty => Included.Count == 0;
}

/// <summary>Decides which of the selected tracks can really be dragged. Pure; the file-system probe is injected.</summary>
public static class DragPayloadPlanner
{
    public const int DefaultCap = 500;
    /// <summary>Checking the disk costs time on a NAS; above this many files we trust the index instead.</summary>
    public const int DefaultExistenceCheckLimit = 20;

    public static DragPayload Plan(IReadOnlyList<DragCandidate> selection, Func<string, bool> exists,
        int cap = DefaultCap, int existenceCheckLimit = DefaultExistenceCheckLimit)
    {
        var included = new List<DragCandidate>();
        var skipped = new List<DragCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal); // paths are passed on verbatim: no case folding, no normalization
        var checkDisk = selection.Count <= existenceCheckLimit;
        var truncated = 0;

        foreach (var candidate in selection)
        {
            if (string.IsNullOrWhiteSpace(candidate.Path)) continue;
            if (candidate.KnownMissing || (checkDisk && !SafeExists(exists, candidate.Path)))
            {
                skipped.Add(candidate);
                continue;
            }
            if (!seen.Add(candidate.Path)) continue; // the same file selected twice is dragged once
            if (included.Count >= cap) { truncated++; continue; }
            included.Add(candidate);
        }
        return new DragPayload(included, skipped, truncated);
    }

    private static bool SafeExists(Func<string, bool> exists, string path)
    {
        try { return exists(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
