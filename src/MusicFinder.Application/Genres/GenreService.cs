using MusicFinder.Application.Services;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Entities;
using MusicFinder.Domain.Enums;
using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Genres;

public sealed class GenreService : IGenreService
{
    private const int MaxNameLength = 80;
    private readonly IGenreRepository _genres;
    private readonly ITrackGenreRepository _trackGenres;
    private readonly GenreMappingJob _job;

    public event EventHandler? TaxonomyChanged;

    public GenreService(IGenreRepository genres, ITrackGenreRepository trackGenres, GenreMappingJob job)
    {
        _genres = genres;
        _trackGenres = trackGenres;
        _job = job;
    }

    public async Task<IReadOnlyList<GenreNode>> GetTaxonomyAsync(CancellationToken ct = default)
    {
        var all = await _genres.GetAllAsync(ct);
        var counts = await _genres.GetTrackCountsAsync(ct);
        var byParent = all.ToLookup(g => g.ParentId);

        GenreNode Build(Genre g) => new(g, counts.GetValueOrDefault(g.Id), byParent[g.Id].Select(Build).ToList());
        return byParent[null].Select(Build).ToList();
    }

    public async Task<IReadOnlyList<(string Alias, string GenreName)>> GetAliasesAsync(CancellationToken ct = default)
    {
        var names = (await _genres.GetAllAsync(ct)).ToDictionary(g => g.Id, g => g.Name);
        return (await _genres.GetAliasesAsync(ct))
            .Where(a => names.ContainsKey(a.GenreId))
            .Select(a => (a.AliasNormalized, names[a.GenreId]))
            .ToList();
    }

    public async Task<IReadOnlyList<UnmappedGenre>> GetUnmappedAsync(CancellationToken ct = default)
    {
        var normalizer = new GenreNormalizer(await _genres.GetAllAsync(ct), await _genres.GetAliasesAsync(ct));
        var found = new Dictionary<string, (string Display, int Count)>(StringComparer.Ordinal);

        foreach (var (raw, count) in await _trackGenres.GetRawGenreCountsAsync(ct))
            foreach (var token in normalizer.Map(raw).Unmapped)
            {
                var key = TextNormalizer.Normalize(token);
                if (key.Length == 0) continue;
                found[key] = found.TryGetValue(key, out var seen) ? (seen.Display, seen.Count + count) : (token, count);
            }

        return found.Values.OrderByDescending(v => v.Count).ThenBy(v => v.Display, StringComparer.CurrentCultureIgnoreCase)
            .Select(v => new UnmappedGenre(v.Display, v.Count)).ToList();
    }

    public async Task<long> AddGenreAsync(string name, long? parentId, CancellationToken ct = default)
    {
        var (clean, normalized) = await ValidateNameAsync(name, exceptId: null, ct);
        if (parentId is { } p && await FindAsync(p, ct) is null) throw new TaxonomyException("The parent genre no longer exists.");

        var id = await _genres.AddAsync(clean, normalized, parentId, ct);
        await ChangedAsync(remap: true, ct);
        return id;
    }

    public async Task RenameGenreAsync(long id, string name, CancellationToken ct = default)
    {
        var genre = await FindAsync(id, ct) ?? throw new TaxonomyException("That genre no longer exists.");
        var (clean, normalized) = await ValidateNameAsync(name, exceptId: id, ct);
        if (clean == genre.Name) return;

        await _genres.UpdateNameAsync(id, clean, normalized, ct);
        await _genres.RemoveAliasAsync(normalized, ct); // the name now says the same thing; drop the redundant alias
        // Files tagged with the old name must keep mapping, so the old name lives on as an alias.
        if (genre.NormalizedName != normalized) await _genres.AddAliasAsync(genre.NormalizedName, id, ct);
        await ChangedAsync(remap: true, ct);
    }

    public async Task MoveGenreAsync(long id, long? newParentId, CancellationToken ct = default)
    {
        var all = await _genres.GetAllAsync(ct);
        var genre = all.FirstOrDefault(g => g.Id == id) ?? throw new TaxonomyException("That genre no longer exists.");
        if (genre.ParentId == newParentId) return;

        if (newParentId is { } target)
        {
            if (all.All(g => g.Id != target)) throw new TaxonomyException("The new parent genre no longer exists.");
            for (long? cursor = target; cursor is { } c; cursor = all.FirstOrDefault(g => g.Id == c)?.ParentId)
                if (c == id) throw new TaxonomyException($"'{genre.Name}' cannot be moved inside itself or one of its sub-genres.");
        }

        await _genres.SetParentAsync(id, newParentId, ct);
        await ChangedAsync(remap: false, ct); // hierarchy only changes filtering, not how raw text maps
    }

    public async Task DeleteGenreAsync(long id, CancellationToken ct = default)
    {
        var all = await _genres.GetAllAsync(ct);
        var genre = all.FirstOrDefault(g => g.Id == id) ?? throw new TaxonomyException("That genre no longer exists.");
        if (all.Any(g => g.ParentId == id))
            throw new TaxonomyException($"'{genre.Name}' still has sub-genres. Move or delete them first.");

        await _genres.DeleteAsync(id, ct);
        await ChangedAsync(remap: true, ct);
    }

    public async Task AddAliasAsync(string alias, long genreId, CancellationToken ct = default)
    {
        var normalized = TextNormalizer.Normalize(alias);
        if (normalized.Length == 0) throw new TaxonomyException("An alias needs at least one letter or digit.");

        var all = await _genres.GetAllAsync(ct);
        var target = all.FirstOrDefault(g => g.Id == genreId) ?? throw new TaxonomyException("That genre no longer exists.");
        if (all.FirstOrDefault(g => g.NormalizedName == normalized) is { } named)
            throw new TaxonomyException($"'{alias.Trim()}' is already the name of the genre '{named.Name}'.");

        if (!await _genres.AddAliasAsync(normalized, genreId, ct))
        {
            var existing = (await _genres.GetAliasesAsync(ct)).First(a => a.AliasNormalized == normalized);
            var owner = all.FirstOrDefault(g => g.Id == existing.GenreId)?.Name ?? "another genre";
            throw new TaxonomyException(existing.GenreId == genreId
                ? $"'{target.Name}' already has that alias."
                : $"'{alias.Trim()}' is already an alias of '{owner}'.");
        }
        await ChangedAsync(remap: true, ct);
    }

    public async Task RemoveAliasAsync(string alias, CancellationToken ct = default)
    {
        await _genres.RemoveAliasAsync(TextNormalizer.Normalize(alias), ct);
        await ChangedAsync(remap: true, ct);
    }

    public async Task SetUserGenreAsync(IReadOnlyList<long> trackIds, long genreId, bool assigned, CancellationToken ct = default)
    {
        await _trackGenres.SetUserGenreAsync(trackIds, genreId, assigned, ct);
        TaxonomyChanged?.Invoke(this, EventArgs.Empty); // counts and "Uncategorized" change; no remap needed
    }

    public Task<IReadOnlyList<(long TrackId, long GenreId, GenreSource Source)>> GetAssignmentsAsync(
        IReadOnlyList<long> trackIds, CancellationToken ct = default) => _trackGenres.GetForTracksAsync(trackIds, ct);

    private async Task<Genre?> FindAsync(long id, CancellationToken ct) =>
        (await _genres.GetAllAsync(ct)).FirstOrDefault(g => g.Id == id);

    private async Task<(string Name, string Normalized)> ValidateNameAsync(string name, long? exceptId, CancellationToken ct)
    {
        var clean = (name ?? "").Trim();
        var normalized = TextNormalizer.Normalize(clean);
        if (normalized.Length == 0) throw new TaxonomyException("A genre needs a name with at least one letter or digit.");
        if (clean.Length > MaxNameLength) throw new TaxonomyException($"Genre names are limited to {MaxNameLength} characters.");

        if ((await _genres.GetAllAsync(ct)).FirstOrDefault(g => g.NormalizedName == normalized && g.Id != exceptId) is { } dup)
            throw new TaxonomyException($"A genre named '{dup.Name}' already exists.");
        // An alias of the same genre is not a conflict (renaming "Laiko" to "Λαϊκό" is fine); any other alias is.
        if ((await _genres.GetAliasesAsync(ct)).Any(a => a.AliasNormalized == normalized && a.GenreId != exceptId))
            throw new TaxonomyException($"'{clean}' is already used as an alias. Remove the alias first.");
        return (clean, normalized);
    }

    private async Task ChangedAsync(bool remap, CancellationToken ct)
    {
        if (remap)
        {
            await _genres.BumpTaxonomyVersionAsync(ct);
            await _job.RunAsync(ct);
        }
        TaxonomyChanged?.Invoke(this, EventArgs.Empty);
    }
}
