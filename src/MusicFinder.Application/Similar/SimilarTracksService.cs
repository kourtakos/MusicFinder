using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Similar;

public sealed class SimilarTracksService : ISimilarTracksService
{
    private readonly ISimilarCandidateSource _source;
    private readonly IGenreRepository _genres;
    private readonly IDjTagService _tags;
    private readonly ISearchService _search;

    public SimilarTracksService(ISimilarCandidateSource source, IGenreRepository genres, IDjTagService tags, ISearchService search)
    {
        _source = source;
        _genres = genres;
        _tags = tags;
        _search = search;
    }

    public async Task<SimilarTracksResult> FindAsync(long trackId, SimilarTracksOptions options, CancellationToken ct)
    {
        var seed = await _source.GetSeedAsync(trackId, ct);
        if (seed is null) return new SimilarTracksResult(null, SimilarityStatus.SeedNotFound, "That track is no longer in the library.", []);

        var seedRow = (await _search.GetRowsAsync([trackId], null, ct)).FirstOrDefault();

        // Fewer than two of genre, BPM and key cannot support a comparison: say so instead of showing a confident-looking list.
        if (SimilarityScorer.SignalCount(seed) < 2)
            return new SimilarTracksResult(seedRow, SimilarityStatus.InsufficientData,
                "This track needs at least two of genre, BPM and key to be compared. MusicFinder does not guess missing values; add them in the tag editor.", []);

        var relations = new GenreRelations(await _genres.GetAllAsync(ct));
        var tagNames = (await _tags.GetAllAsync(ct)).ToDictionary(t => t.Tag.Id, t => t.Tag.Name);
        var scorer = new SimilarityScorer(options, relations, tagNames);

        var candidates = await _source.GetCandidatesAsync(seed, options, relations.RelatedIds(seed.GenreIds), ct);

        var scored = new List<(SimilarSignals Track, SimilarityScore Score)>(candidates.Count);
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (candidate.TrackId == seed.TrackId) continue;           // never the same file
            if (scorer.Score(seed, candidate) is { } score) scored.Add((candidate, score));
        }
        scored.Sort(SimilarityScorer.Compare);

        var top = scored.Take(Math.Max(1, options.MaxResults)).ToList();
        var rows = (await _search.GetRowsAsync(top.Select(t => t.Track.TrackId).ToList(), null, ct)).ToDictionary(r => r.Id);

        var items = top.Where(t => rows.ContainsKey(t.Track.TrackId))
            .Select(t => new SimilarTrack(rows[t.Track.TrackId], t.Score.Score, t.Score.AvailableWeight, t.Score.Reasons, t.Score.UsedHalfDoubleTempo))
            .ToList();

        return new SimilarTracksResult(seedRow, SimilarityStatus.Ok,
            items.Count == 0 ? "No similar tracks found in your library with these settings." : null, items);
    }
}
