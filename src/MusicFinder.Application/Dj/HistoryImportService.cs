using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Dj;

/// <inheritdoc cref="IHistoryImportService"/>
public sealed class HistoryImportService : IHistoryImportService
{
    private readonly IReadOnlyList<IHistoryImporter> _importers;
    private readonly HistoryTrackMatcher _matcher;
    private readonly IHistoryImportStore _store;
    private readonly IImportedPlayRecorder _recorder;
    private readonly ISettingsStore _settings;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _running = new(1, 1);

    public HistoryImportService(IEnumerable<IHistoryImporter> importers, HistoryTrackMatcher matcher, IHistoryImportStore store,
        IImportedPlayRecorder recorder, ISettingsStore settings, ILogger<HistoryImportService>? log = null)
    {
        _importers = importers.ToList();
        _matcher = matcher;
        _store = store;
        _recorder = recorder;
        _settings = settings;
        _log = log ?? NullLogger<HistoryImportService>.Instance;
    }

    public IReadOnlyList<IHistoryImporter> Importers => _importers;

    public event EventHandler<IReadOnlyList<HistoryImportResult>>? ImportCompleted;

    public string? ResolveFolder(string providerId)
    {
        var importer = _importers.FirstOrDefault(i => i.ProviderId == providerId);
        if (importer is null) return null;
        var chosen = ChosenFolder(providerId);
        if (string.IsNullOrWhiteSpace(chosen)) return importer.DetectDefaultFolders().FirstOrDefault();
        if (Directory.Exists(chosen)) return chosen;
        // Settings travel between PCs (restore, a copied portable folder): a folder chosen on another machine may not exist here.
        // The detected one is used then; with none, the chosen path is returned so the import reports it as missing.
        return importer.DetectDefaultFolders().FirstOrDefault() ?? chosen;
    }

    /// <summary>True when the folder the user chose does not exist on this PC (so the detected one is used instead, if there is one).</summary>
    public bool ChosenFolderIsMissing(string providerId) =>
        ChosenFolder(providerId) is { Length: > 0 } chosen && !Directory.Exists(chosen);

    private string? ChosenFolder(string providerId)
    {
        var settings = _settings.Load();
        return providerId == PlaySources.VirtualDj ? settings.VirtualDjHistoryFolder
            : providerId == PlaySources.Traktor ? settings.TraktorHistoryFolder
            : null;
    }

    public async Task<IReadOnlyList<HistoryImportResult>> RunAsync(string? providerId, CancellationToken ct = default)
    {
        await _running.WaitAsync(ct);
        try
        {
            var results = new List<HistoryImportResult>();
            foreach (var importer in _importers.Where(i => providerId is null || i.ProviderId == providerId))
            {
                ct.ThrowIfCancellationRequested();
                results.Add(await ImportOneAsync(importer, ct));
            }
            ImportCompleted?.Invoke(this, results);
            return results;
        }
        finally { _running.Release(); }
    }

    private async Task<HistoryImportResult> ImportOneAsync(IHistoryImporter importer, CancellationToken ct)
    {
        var folder = ResolveFolder(importer.ProviderId);
        HistoryImportResult Fail(string message) => new(importer.ProviderId, importer.DisplayName, folder, 0, 0, 0, 0, 0, 0, 0, [], message);

        if (folder is null) return Fail($"No {importer.DisplayName} history folder was found. Choose it in Settings.");
        if (!Directory.Exists(folder)) return Fail($"The {importer.DisplayName} history folder does not exist: {folder}");

        try
        {
            var known = await _store.GetFileStatesAsync(importer.ProviderId, ct);

            // Plays that found no track earlier may have one now (the file was indexed since); try those first.
            var (relinked, relinkedUpgraded) = await RetryPendingAsync(importer.ProviderId, ct);

            var read = await importer.ReadAsync(folder, known, ct);
            if (read.Error is not null) return new(importer.ProviderId, importer.DisplayName, folder, read.FilesRead, 0, 0, 0, 0, 0, 0, read.Warnings, read.Error);

            int added = relinked - relinkedUpgraded, upgraded = relinkedUpgraded, duplicates = 0, unmatched = 0, ambiguous = 0;
            foreach (var play in read.Events.OrderBy(e => e.PlayedUtc).ThenBy(e => e.ExternalEventKey, StringComparer.Ordinal))
            {
                var match = await _matcher.MatchAsync(play, ct);
                if (match.TrackId is { } trackId)
                {
                    switch (await _recorder.RecordImportedPlayAsync(play, trackId, match.Method!, match.Confidence, ct))
                    {
                        case ImportedPlayOutcome.Added: added++; break;
                        case ImportedPlayOutcome.Upgraded: upgraded++; break;
                        default: duplicates++; break;
                    }
                    continue;
                }

                var isNew = await _store.AddUnmatchedAsync(ToUnmatched(play, match), ct);
                if (!isNew) { duplicates++; continue; }
                if (match.Reason == HistoryUnmatchedReason.Ambiguous) ambiguous++; else unmatched++;
            }

            // Remembered last: a file whose plays were not all stored is read again next time.
            foreach (var file in read.ReadFiles) await _store.SaveFileStateAsync(importer.ProviderId, file, ct);

            return new(importer.ProviderId, importer.DisplayName, folder, read.FilesRead, read.Events.Count + relinked, added, upgraded, duplicates, unmatched, ambiguous, read.Warnings, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogError(ex, "Importing {Provider} history failed.", importer.ProviderId);
            return Fail($"Reading the {importer.DisplayName} history failed: {ex.Message}");
        }
    }

    private async Task<(int Relinked, int Upgraded)> RetryPendingAsync(string source, CancellationToken ct)
    {
        int relinked = 0, upgraded = 0;
        foreach (var pending in (await _store.GetUnmatchedAsync(UnmatchedStatus.Pending, ct)).Where(p => p.Source == source))
        {
            var play = ToPlay(pending);
            var match = await _matcher.MatchAsync(play, ct);
            if (match.TrackId is not { } trackId) continue;
            var outcome = await _recorder.RecordImportedPlayAsync(play, trackId, match.Method!, match.Confidence, ct);
            await _store.SetUnmatchedStatusAsync(pending.Id, UnmatchedStatus.Linked, trackId, ct);
            relinked++;
            if (outcome == ImportedPlayOutcome.Upgraded) upgraded++;
        }
        return (relinked, upgraded);
    }

    public async Task<ImportedPlayOutcome> LinkAsync(long unmatchedId, long trackId, CancellationToken ct = default)
    {
        var pending = (await _store.GetUnmatchedAsync(UnmatchedStatus.Pending, ct)).FirstOrDefault(p => p.Id == unmatchedId)
            ?? throw new InvalidOperationException("That play is no longer waiting for a track.");
        var outcome = await _recorder.RecordImportedPlayAsync(ToPlay(pending), trackId, HistoryMatchMethod.Manual, 1.0, ct);
        await _store.SetUnmatchedStatusAsync(unmatchedId, UnmatchedStatus.Linked, trackId, ct);
        return outcome;
    }

    public Task IgnoreAsync(long unmatchedId, CancellationToken ct = default) =>
        _store.SetUnmatchedStatusAsync(unmatchedId, UnmatchedStatus.Ignored, null, ct);

    private static UnmatchedHistoryEvent ToUnmatched(ImportedPlayEvent play, HistoryMatch match) =>
        new(0, play.Source, play.ExternalEventKey, play.PlayedUtc, play.FilePath, play.Artist, play.Title, play.FileSize, play.DurationSeconds,
            match.Reason ?? HistoryUnmatchedReason.NotInLibrary,
            match.Candidates.Count > 1 ? System.Text.Json.JsonSerializer.Serialize(match.Candidates) : null, UnmatchedStatus.Pending, null);

    private static ImportedPlayEvent ToPlay(UnmatchedHistoryEvent e) =>
        new(e.Source, e.ExternalEventKey, e.PlayedUtc, e.SourcePath, e.Artist, e.Title, null, e.FileSize, e.DurationSeconds);
}
