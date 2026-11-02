using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Entities;

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
            return new AddRootResult(false, $"'{path}' is not a valid folder path.", null);
        }

        if (!Directory.Exists(full))
            return new AddRootResult(false, $"The folder '{full}' does not exist or is not accessible.", null);

        foreach (var existing in await _roots.GetAllAsync())
        {
            if (Overlaps(existing.Path, full, out var relation))
                return new AddRootResult(false, $"'{full}' {relation} the already indexed folder '{existing.Path}'.", null);
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
