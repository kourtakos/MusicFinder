using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MusicFinder.Application.Dj;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.Infrastructure.Dj;

/// <summary>
/// Reads VirtualDJ's History folder: one <c>yyyy-MM-dd.m3u</c> file per day. Read-only (the files are opened shared, so VirtualDJ can keep writing),
/// and files that did not change since the last pass are skipped.
/// </summary>
public sealed class VirtualDjHistoryImporter : IHistoryImporter
{
    private readonly TimeZoneInfo _zone;
    private readonly string _localAppData;
    private readonly string _documents;
    private readonly ILogger _log;

    public VirtualDjHistoryImporter(ILogger<VirtualDjHistoryImporter>? log = null)
        : this(TimeZoneInfo.Local, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
               Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), log) { }

    public VirtualDjHistoryImporter(TimeZoneInfo zone, string localAppData, string documents, ILogger? log = null)
    {
        _zone = zone;
        _localAppData = localAppData;
        _documents = documents;
        _log = log ?? NullLogger.Instance;
    }

    public string ProviderId => PlaySources.VirtualDj;
    public string DisplayName => "VirtualDJ";
    public HistorySyncMode SyncMode => HistorySyncMode.Live;

    /// <summary>The first is the folder seen on a VirtualDJ 2026 install; the second is where older versions kept it (not verified).</summary>
    public IReadOnlyList<string> DetectDefaultFolders() =>
        new[] { Path.Combine(_localAppData, "VirtualDJ", "History"), Path.Combine(_documents, "VirtualDJ", "History") }
            .Where(Directory.Exists).ToList();

    public Task<HistoryReadResult> ReadAsync(string folder, IReadOnlyDictionary<string, HistoryFileState> known, CancellationToken ct) =>
        Task.Run(() => Read(folder, known, ct), ct);

    private HistoryReadResult Read(string folder, IReadOnlyDictionary<string, HistoryFileState> known, CancellationToken ct)
    {
        var events = new List<ImportedPlayEvent>();
        var read = new List<HistoryFileState>();
        var warnings = new List<string>();
        var filesRead = 0;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(folder, "*.m3u").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new([], [], [], 0, $"The VirtualDJ history folder cannot be read: {ex.Message}");
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            if (!DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                continue;                                              // not a daily history file

            try
            {
                var info = new FileInfo(file);
                var state = new HistoryFileState(file, info.Length, info.LastWriteTimeUtc);
                if (known.TryGetValue(file, out var before) && before.Length == state.Length && (before.LastWriteUtc - state.LastWriteUtc).Duration() < TimeSpan.FromSeconds(1))
                    continue;                                          // unchanged since it was read

                var bytes = ReadShared(file);
                var parsed = VirtualDjHistoryParser.Parse(bytes, day, _zone);
                filesRead++;
                events.AddRange(parsed.Events);
                warnings.AddRange(parsed.Warnings.Select(w => $"{name}: {w}"));
                // The size is taken from the bytes read, so a file that grew meanwhile is read again; one still being written is left for next time.
                if (!parsed.HasIncompleteTail) read.Add(state with { Length = bytes.Length });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "Could not read VirtualDJ history file {File}.", file);
                warnings.Add($"{name}: could not be read ({ex.Message}).");
            }
        }
        return new(events, read, warnings, filesRead);
    }

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
