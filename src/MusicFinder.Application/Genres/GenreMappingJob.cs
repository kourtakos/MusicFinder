using MusicFinder.Domain.Contracts;

namespace MusicFinder.Application.Genres;

/// <summary>
/// Brings TrackGenres up to date with the current taxonomy. Works purely on stored GenreRaw text (no file reads),
/// in small batches, so it is cheap to run after scans, taxonomy edits and tag writes.
/// </summary>
public sealed class GenreMappingJob
{
    private const int BatchSize = 500;
    private readonly IGenreRepository _genres;
    private readonly ITrackGenreRepository _trackGenres;

    public GenreMappingJob(IGenreRepository genres, ITrackGenreRepository trackGenres)
    {
        _genres = genres;
        _trackGenres = trackGenres;
    }

    /// <summary>Returns the number of tracks that were (re)mapped.</summary>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var version = await _genres.GetTaxonomyVersionAsync(ct);
        var normalizer = new GenreNormalizer(await _genres.GetAllAsync(ct), await _genres.GetAliasesAsync(ct));

        var total = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var stale = await _trackGenres.GetStaleAsync(version, BatchSize, ct);
            if (stale.Count == 0) break;

            var mappings = stale
                .Select(t => new TrackGenreMapping(t.TrackId, t.RawGenre, normalizer.Map(t.RawGenre).GenreIds))
                .ToList();
            var applied = await _trackGenres.ReplaceEmbeddedAsync(mappings, version, ct);
            total += applied;

            // Every row in the batch was skipped (raw genre changed under us): stop instead of spinning;
            // the next run picks them up.
            if (applied == 0) break;
        }
        return total;
    }
}
