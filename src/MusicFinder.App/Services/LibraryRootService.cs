using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Entities;
using MusicFinder.App.Localization;

namespace MusicFinder.App.Services;

public sealed record AddRootResult(bool Success, string? Message, LibraryRoot? Root);

/// <summary>Adds, enables/disables and rescans library roots. Never touches the music files themselves.</summary>
public sealed class LibraryRootService
{
    private readonly ILibraryRootRepository _roots;
    private readonly ScanCoordinator _scans;

    /// <summary>Raised on the calling (UI) context after the set of roots or their enabled state changed.</summary>
    public event EventHandler? RootsChanged;

    public LibraryRootService(ILibraryRootRepository roots, ScanCoordinator scans)
    {
        _roots = roots;
        _scans = scans;
    }

    public async Task<AddRootResult> AddAsync(string path)
    {
        string full;
        try { full = Canonicalize(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new AddRootResult(false, Loc.T("'{0}' is not a valid folder path.", path), null);
        }

        if (!Directory.Exists(full))
            return new AddRootResult(false, Loc.T("The folder '{0}' does not exist or is not accessible.", full), null);

        foreach (var existing in await _roots.GetAllAsync())
        {
            if (Overlaps(existing.Path, full, out var relation))
                return new AddRootResult(false, relation switch
                {
                    "is the same as" => Loc.T("'{0}' is the same as the already indexed folder '{1}'.", full, existing.Path),
                    "is inside" => Loc.T("'{0}' is inside the already indexed folder '{1}'.", full, existing.Path),
                    _ => Loc.T("'{0}' contains the already indexed folder '{1}'.", full, existing.Path)
                }, null);
        }

        var root = await _roots.AddAsync(full);
        _scans.Enqueue(root.Id);
        RootsChanged?.Invoke(this, EventArgs.Empty);
        return new AddRootResult(true, null, root);
    }

    public async Task SetEnabledAsync(long rootId, bool enabled)
    {
        await _roots.SetEnabledAsync(rootId, enabled);
        if (enabled) _scans.Enqueue(rootId);
        RootsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Rescan(long rootId) => _scans.Enqueue(rootId);

    public async Task RescanAllAsync()
    {
        foreach (var root in await _roots.GetAllAsync())
            if (root.Enabled) _scans.Enqueue(root.Id);
    }

    public static string Canonicalize(string path)
    {
        var full = Path.GetFullPath(path.Trim().Trim('"'));
        var rootOfPath = Path.GetPathRoot(full);
        return full == rootOfPath ? full : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    internal static bool Overlaps(string existing, string candidate, out string relation)
    {
        relation = "";
        if (string.Equals(existing, candidate, StringComparison.OrdinalIgnoreCase)) { relation = "is the same as"; return true; }
        if (IsUnder(candidate, existing)) { relation = "is inside"; return true; }
        if (IsUnder(existing, candidate)) { relation = "contains"; return true; }
        return false;
    }

    private static bool IsUnder(string child, string parent)
    {
        var p = parent.EndsWith(Path.DirectorySeparatorChar) ? parent : parent + Path.DirectorySeparatorChar;
        return child.StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }
}
