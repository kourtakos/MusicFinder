using MusicFinder.Application.Services;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Genres;

public sealed class DjTagService : IDjTagService
{
    private const int MaxNameLength = 40;
    private readonly ICustomTagRepository _tags;

    public event EventHandler? TagsChanged;

    public DjTagService(ICustomTagRepository tags) => _tags = tags;

    public async Task<IReadOnlyList<DjTagInfo>> GetAllAsync(CancellationToken ct = default)
    {
        var counts = await _tags.GetTrackCountsAsync(ct);
        return (await _tags.GetAllAsync(ct)).Select(t => new DjTagInfo(t, counts.GetValueOrDefault(t.Id))).ToList();
    }

    public async Task<long> AddAsync(string name, CancellationToken ct = default)
    {
        var clean = await ValidateAsync(name, exceptId: null, ct);
        var id = await _tags.AddAsync(clean, ct);
        Raise();
        return id;
    }

    public async Task RenameAsync(long id, string name, CancellationToken ct = default)
    {
        var clean = await ValidateAsync(name, exceptId: id, ct);
        await _tags.RenameAsync(id, clean, ct);
        Raise();
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await _tags.DeleteAsync(id, ct);
        Raise();
    }

    public async Task AssignAsync(IReadOnlyList<long> trackIds, long tagId, CancellationToken ct = default)
    {
        await _tags.AssignAsync(trackIds, tagId, ct);
        Raise();
    }

    public async Task UnassignAsync(IReadOnlyList<long> trackIds, long tagId, CancellationToken ct = default)
    {
        await _tags.UnassignAsync(trackIds, tagId, ct);
        Raise();
    }

    public async Task<IReadOnlyDictionary<long, IReadOnlySet<long>>> GetAssignmentsAsync(IReadOnlyList<long> trackIds, CancellationToken ct = default) =>
        (await _tags.GetAssignmentsAsync(trackIds, ct))
            .GroupBy(a => a.TrackId)
            .ToDictionary(g => g.Key, g => (IReadOnlySet<long>)g.Select(a => a.TagId).ToHashSet());

    private async Task<string> ValidateAsync(string name, long? exceptId, CancellationToken ct)
    {
        var clean = (name ?? "").Trim();
        var key = TextNormalizer.Normalize(clean);
        if (key.Length == 0) throw new TaxonomyException("A tag needs a name with at least one letter or digit.");
        if (clean.Length > MaxNameLength) throw new TaxonomyException($"Tag names are limited to {MaxNameLength} characters.");
        if ((await _tags.GetAllAsync(ct)).FirstOrDefault(t => t.Id != exceptId && TextNormalizer.Normalize(t.Name) == key) is { } dup)
            throw new TaxonomyException($"A tag named '{dup.Name}' already exists.");
        return clean;
    }

    private void Raise() => TagsChanged?.Invoke(this, EventArgs.Empty);
}
