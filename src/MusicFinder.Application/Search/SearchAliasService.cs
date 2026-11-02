using MusicFinder.Application.Services;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Search;

public sealed class SearchAliasService : ISearchAliasService
{
    private const int MaxLength = 100;
    private readonly ISearchAliasOverrideRepository _overrides;
    private readonly ITrackSearchAliasRepository _aliases;
    private readonly SearchAliasIndexJob _job;

    public event EventHandler? AliasesChanged;

    public SearchAliasService(ISearchAliasOverrideRepository overrides, ITrackSearchAliasRepository aliases, SearchAliasIndexJob job)
    {
        _overrides = overrides;
        _aliases = aliases;
        _job = job;
    }

    public Task<IReadOnlyList<SearchAliasOverride>> GetAllAsync(CancellationToken ct = default) => _overrides.GetAllAsync(ct);

    public async Task<SearchAliasResult> AddAsync(string source, string alias, CancellationToken ct = default)
    {
        source = (source ?? "").Trim();
        alias = (alias ?? "").Trim();
        var sourceN = TextNormalizer.Normalize(source);
        var aliasN = TextNormalizer.Normalize(alias);

        if (sourceN.Length == 0 || aliasN.Length == 0) return Fail("Enter both the name as it is tagged and the spelling you want to search by.");
        if (source.Length > MaxLength || alias.Length > MaxLength) return Fail($"Keep each side under {MaxLength} characters.");
        if (sourceN == aliasN) return Fail("Both sides are the same once case and accents are ignored; search already finds that.");

        var added = await _overrides.AddAsync(source, alias, sourceN, aliasN, ct);
        if (added is null) return Fail("That alias already exists.");

        // Only the tracks that contain the tagged name are rebuilt, not the whole library.
        await _aliases.MarkStaleAsync(await _aliases.FindContainingAsync(sourceN, ct), ct);
        await _job.RunAsync(ct);
        AliasesChanged?.Invoke(this, EventArgs.Empty);
        return new SearchAliasResult(true, null, added);
    }

    public async Task RemoveAsync(long id, CancellationToken ct = default)
    {
        var removed = await _overrides.RemoveAsync(id, ct);
        if (removed is null) return;

        await _aliases.MarkStaleAsync(await _aliases.FindWithExtraAsync(removed.AliasNormalized, ct), ct);
        await _job.RunAsync(ct);
        AliasesChanged?.Invoke(this, EventArgs.Empty);
    }

    private static SearchAliasResult Fail(string message) => new(false, message, null);
}
