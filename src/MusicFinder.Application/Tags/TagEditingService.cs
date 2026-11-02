using System.Globalization;
using MusicFinder.Application.Genres;
using MusicFinder.Application.Services;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Entities;
using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Tags;

public sealed class TagEditingService : ITagEditingService
{
    private readonly ITrackRepository _tracks;
    private readonly IMetadataWriter _writer;
    private readonly GenreMappingJob _genreJob;

    public event EventHandler<IReadOnlyList<long>>? TracksChanged;

    public TagEditingService(ITrackRepository tracks, IMetadataWriter writer, GenreMappingJob genreJob)
    {
        _tracks = tracks;
        _writer = writer;
        _genreJob = genreJob;
    }

    public async Task<IReadOnlyDictionary<TagField, IReadOnlyList<string?>>> GetFieldValuesAsync(
        IReadOnlyList<long> trackIds, CancellationToken ct = default)
    {
        var tracks = await _tracks.GetManyAsync(trackIds, ct);
        return Enum.GetValues<TagField>().ToDictionary(
            field => field,
            field => (IReadOnlyList<string?>)tracks
                .Select(t => Canonical(field, CurrentValue(t, field)))
                .Distinct()
                .OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase)
                .ToList());
    }

    public async Task<TagEditPlan> PreviewAsync(IReadOnlyList<long> trackIds, TagChangeSet changes, CancellationToken ct = default)
    {
        var errors = _writer.Validate(changes);
        if (errors.Count > 0) return new TagEditPlan(errors, [], 0, 0);

        var rows = new List<TagDiffRow>();
        var changed = 0;
        var unchanged = 0;
        foreach (var track in (await _tracks.GetManyAsync(trackIds, ct)).OrderBy(t => t.FullPath, StringComparer.OrdinalIgnoreCase))
        {
            var effective = EffectiveChanges(track, changes);
            if (effective.Count == 0) { unchanged++; continue; }
            changed++;
            foreach (var (field, after) in effective)
                rows.Add(new TagDiffRow(track.Id, track.FileName, field, CurrentValue(track, field), after));
        }
        return new TagEditPlan([], rows, changed, unchanged);
    }

    public async Task<TagEditSummary> ApplyAsync(IReadOnlyList<long> trackIds, TagChangeSet changes, TagWriteOptions options,
        IProgress<TagEditProgress>? progress, CancellationToken ct)
    {
        var errors = _writer.Validate(changes);
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors), nameof(changes));

        var tracks = (await _tracks.GetManyAsync(trackIds, ct)).OrderBy(t => t.FullPath, StringComparer.OrdinalIgnoreCase).ToList();
        var results = new List<TagEditFileResult>();
        var written = new List<long>();
        var cancelled = false;

        for (var i = 0; i < tracks.Count; i++)
        {
            if (ct.IsCancellationRequested) { cancelled = true; break; } // stop between files; finished files stay finished
            var track = tracks[i];
            progress?.Report(new TagEditProgress(i, tracks.Count, track.FileName));

            var effective = EffectiveChanges(track, changes);
            if (effective.Count == 0)
            {
                results.Add(new TagEditFileResult(track.Id, track.FullPath, TagEditOutcome.Unchanged)); // never touch a file that already matches
                continue;
            }

            TagWriteResult result;
            try
            {
                result = await _writer.WriteAsync(track.FullPath, new TagChangeSet(effective.ToDictionary(e => e.Key, e => e.Value)), options, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                break;
            }

            if (!result.Success)
            {
                results.Add(new TagEditFileResult(track.Id, track.FullPath, TagEditOutcome.Failed, result.Error));
                continue;
            }

            // The file is written and verified; now bring the database in step. If this step fails the file is
            // still correct, and because the stored size/time are stale the next scan will re-read it.
            try
            {
                await _tracks.ApplyMetadataAsync(track.Id, result.After!, result.NewSize!.Value, result.NewModifiedUtc!.Value,
                    NormalizedTextFor(track.FileName, result.After!), CancellationToken.None);
                written.Add(track.Id);
                results.Add(new TagEditFileResult(track.Id, track.FullPath, TagEditOutcome.Written, BackupPath: result.BackupPath));
            }
            catch (Exception ex)
            {
                results.Add(new TagEditFileResult(track.Id, track.FullPath, TagEditOutcome.Written, BackupPath: result.BackupPath,
                    Warning: "The file was updated but the library could not be refreshed (" + ex.Message + "). Rescan the folder to pick it up."));
            }
        }

        progress?.Report(new TagEditProgress(tracks.Count, tracks.Count, ""));

        if (written.Count > 0)
        {
            await _genreJob.RunAsync(CancellationToken.None); // genre text may have changed
            TracksChanged?.Invoke(this, written);
        }
        return new TagEditSummary(results, cancelled);
    }

    // ===== diffing =====

    /// <summary>Only the fields whose canonical new value differs from what the track has now.</summary>
    private static IReadOnlyDictionary<TagField, string?> EffectiveChanges(Track track, TagChangeSet changes)
    {
        var result = new Dictionary<TagField, string?>();
        foreach (var (field, raw) in changes.Changes)
        {
            var after = TagChangeValidator.Canonicalize(field, raw, out _);
            var before = Canonical(field, CurrentValue(track, field));
            if (!string.Equals(before, after, StringComparison.Ordinal)) result[field] = after;
        }
        return result;
    }

    private static string? Canonical(TagField field, string? current) =>
        TagChangeValidator.Canonicalize(field, current, out _);

    public static string? CurrentValue(Track track, TagField field) => field switch
    {
        TagField.Title => track.Title,
        TagField.Artist => track.Artist,
        TagField.AlbumArtist => track.AlbumArtist,
        TagField.Album => track.Album,
        TagField.Genre => track.GenreRaw,
        TagField.Year => track.Year?.ToString(CultureInfo.InvariantCulture),
        TagField.TrackNumber => track.TrackNumber?.ToString(CultureInfo.InvariantCulture),
        TagField.Bpm => track.Bpm is { } b ? Math.Round(b, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture) : null,
        TagField.Key => track.MusicalKey,
        _ => null
    };

    private static string NormalizedTextFor(string fileName, TrackMetadata m) =>
        TextNormalizer.BuildSearchText(fileName, m.Title, m.Artist, m.AlbumArtist, m.Album, m.GenreRaw);
}
