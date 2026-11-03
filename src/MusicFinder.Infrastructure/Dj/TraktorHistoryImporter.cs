using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.Infrastructure.Dj;

/// <summary>
/// Placeholder. Traktor archives its history when it is closed, and the file format has not been seen on a real installation yet, so nothing is
/// parsed: guessing the format would risk confirming plays that never happened. The folder setting and detection already work, so the
/// parser can be added once real history files exist.
/// </summary>
public sealed class TraktorHistoryImporter : IHistoryImporter
{
    private readonly string _documents;

    public TraktorHistoryImporter() : this(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)) { }

    public TraktorHistoryImporter(string documents) => _documents = documents;

    public string ProviderId => PlaySources.Traktor;
    public string DisplayName => "Traktor";
    public HistorySyncMode SyncMode => HistorySyncMode.AfterClose;

    /// <summary>Documents\Native Instruments\Traktor &lt;version&gt;\History, newest version first. The layout comes from third-party descriptions, not from an install.</summary>
    public IReadOnlyList<string> DetectDefaultFolders()
    {
        var root = Path.Combine(_documents, "Native Instruments");
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateDirectories(root, "Traktor*")
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
            .Select(d => Path.Combine(d, "History"))
            .Where(Directory.Exists).ToList();
    }

    public Task<HistoryReadResult> ReadAsync(string folder, IReadOnlyDictionary<string, HistoryFileState> known, CancellationToken ct) =>
        Task.FromResult(new HistoryReadResult([], [], [], 0,
            "Reading Traktor's history is not available yet: it needs real history files from a Traktor install to build and test the reader."));
}
