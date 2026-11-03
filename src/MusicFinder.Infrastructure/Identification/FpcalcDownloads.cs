namespace MusicFinder.Infrastructure.Identification;

public enum FpcalcCandidateKind { Exe, Zip }

/// <summary>Something in the Downloads folder that looks like Chromaprint's fpcalc. It is only offered to the user, never used without a click.</summary>
public sealed record FpcalcCandidate(string Path, FpcalcCandidateKind Kind);

/// <summary>Looks in the Downloads folder for what a person gets from the Chromaprint download page: the zip, or the folder or file it holds.</summary>
public static class FpcalcDownloads
{
    private const string ZipPattern = "chromaprint-fpcalc-*.zip";
    private const string FolderPattern = "chromaprint-fpcalc-*";

    public static string? DefaultFolder()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile) ? null : Path.Combine(profile, "Downloads");
    }

    /// <summary>Newest first. Looks at the top of the folder and one level into an unzipped chromaprint folder; never searches deeper.</summary>
    public static IReadOnlyList<FpcalcCandidate> Find(string? downloadsFolder)
    {
        if (string.IsNullOrEmpty(downloadsFolder) || !Directory.Exists(downloadsFolder)) return [];
        var found = new List<(FpcalcCandidate Candidate, DateTime Written)>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(downloadsFolder, "fpcalc.exe"))
                found.Add((new FpcalcCandidate(file, FpcalcCandidateKind.Exe), File.GetLastWriteTimeUtc(file)));
            foreach (var zip in Directory.EnumerateFiles(downloadsFolder, ZipPattern))
                found.Add((new FpcalcCandidate(zip, FpcalcCandidateKind.Zip), File.GetLastWriteTimeUtc(zip)));
            foreach (var folder in Directory.EnumerateDirectories(downloadsFolder, FolderPattern))
            {
                var exe = System.IO.Path.Combine(folder, "fpcalc.exe");
                if (File.Exists(exe)) found.Add((new FpcalcCandidate(exe, FpcalcCandidateKind.Exe), File.GetLastWriteTimeUtc(exe)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* an unreadable Downloads folder simply offers nothing */ }
        return found.OrderByDescending(f => f.Written).Select(f => f.Candidate).ToList();
    }
}
