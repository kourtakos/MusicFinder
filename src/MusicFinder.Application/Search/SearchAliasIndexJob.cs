using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Search;

/// <summary>
/// Builds and refreshes the alias rows of smart Greek search, in small batches, from stored text only (no file reads).
/// Shaped like <c>GenreMappingJob</c>: cheap to run after start-up, scans, tag edits and alias changes; a row whose text changed while it
/// was being prepared is skipped and picked up by the next run. Only one run executes at a time.
/// </summary>
public sealed class SearchAliasIndexJob
{
    private const int BatchSize = 500;
    private readonly ITrackSearchAliasRepository _aliases;
    private readonly ISearchAliasOverrideRepository _overrides;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SearchAliasIndexJob(ITrackSearchAliasRepository aliases, ISearchAliasOverrideRepository overrides)
    {
        _aliases = aliases;
        _overrides = overrides;
    }

    /// <summary>Raised after each batch with (rows done in this run, rows still waiting). May come from any thread.</summary>
    public event EventHandler<(int Done, int Remaining)>? Progress;

    /// <summary>Raised when a run finished and changed the index (so cached typo vocabulary can be reloaded).</summary>
    public event EventHandler? Indexed;

    /// <summary>Returns the number of tracks that were (re)indexed.</summary>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        // A run that is already going will also pick up whatever the caller just made stale, so wait for it instead of starting a second one.
        await _gate.WaitAsync(ct);
        try
        {
            var version = SearchTextBuilder.CurrentVersion;
            var overrides = await _overrides.GetAllAsync(ct);

            var total = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var stale = await _aliases.GetStaleAsync(version, BatchSize, ct);
                if (stale.Count == 0) break;

                var rows = stale.Select(s => (Source: s, Alias: SearchTextBuilder.Build(s, overrides))).ToList();
                var applied = await _aliases.UpsertAsync(rows, version, ct);
                total += applied;

                Progress?.Invoke(this, (total, await _aliases.CountStaleAsync(version, ct)));

                // Every row in the batch changed under us: stop instead of spinning; the next run picks them up.
                if (applied == 0) break;
            }

            if (total > 0) Indexed?.Invoke(this, EventArgs.Empty);
            return total;
        }
        finally { _gate.Release(); }
    }
}
