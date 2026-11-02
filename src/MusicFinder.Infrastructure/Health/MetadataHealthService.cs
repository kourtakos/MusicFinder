using Dapper;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Entities;
using MusicFinder.Domain.Enums;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure.Persistence;

namespace MusicFinder.Infrastructure.Health;

/// <summary>
/// Counts findings straight from the Tracks table (one pass, no files opened), so the numbers are always current:
/// a tag edit or a move shows up on the next call without any rescan.
/// </summary>
public sealed class MetadataHealthService : IMetadataHealthService
{
    private readonly SqliteConnectionFactory _factory;
    private readonly ILibraryRootRepository _roots;

    public MetadataHealthService(SqliteConnectionFactory factory, ILibraryRootRepository roots)
    {
        _factory = factory;
        _roots = roots;
    }

    // Blank and NULL are the same thing here: a tag that is only spaces is empty.
    private static string Blank(string column) => $"({column} IS NULL OR TRIM({column}) = '')";

    private static string Predicate(HealthIssueKind kind) => kind switch
    {
        HealthIssueKind.MissingFiles => "t.Missing = 1",
        HealthIssueKind.NotChecked => $"t.Missing = 0 AND t.MetaVersion < {Track.CurrentMetaVersion}",
        HealthIssueKind.MissingTitle => "t.Missing = 0 AND " + Blank("t.Title"),
        HealthIssueKind.MissingArtist => "t.Missing = 0 AND " + Blank("t.Artist"),
        HealthIssueKind.MissingAlbum => "t.Missing = 0 AND " + Blank("t.Album"),
        HealthIssueKind.MissingGenre => "t.Missing = 0 AND " + Blank("t.GenreRaw"),
        HealthIssueKind.MissingYear => "t.Missing = 0 AND t.Year IS NULL",
        HealthIssueKind.MissingBpm => "t.Missing = 0 AND (t.Bpm IS NULL OR t.Bpm <= 0)",
        HealthIssueKind.MissingKey => "t.Missing = 0 AND " + Blank("t.MusicalKey"),
        HealthIssueKind.MissingArtwork => "t.Missing = 0 AND (t.ArtworkCacheKey IS NULL OR t.ArtworkCacheKey = '')",
        HealthIssueKind.Mojibake => Flag(IssueFlags.Mojibake),
        HealthIssueKind.Unreadable => Flag(IssueFlags.Unreadable),
        HealthIssueKind.LowBitrate => Flag(IssueFlags.LowBitrate),
        HealthIssueKind.OddSampleRate => Flag(IssueFlags.OddSampleRate),
        HealthIssueKind.CodecMismatch => Flag(IssueFlags.CodecMismatch),
        HealthIssueKind.SizeMismatch => Flag(IssueFlags.SizeMismatch),
        HealthIssueKind.ZeroDuration => Flag(IssueFlags.ZeroDuration),
        _ => "1 = 0"
    };

    private static string Flag(IssueFlags f) => $"t.Missing = 0 AND (t.IssueFlags & {(int)f}) <> 0";

    public async Task<HealthSummary> GetSummaryAsync(CancellationToken ct)
    {
        var kinds = Enum.GetValues<HealthIssueKind>();
        var columns = string.Join(", ", kinds.Select(k => $"COALESCE(SUM(CASE WHEN {Predicate(k)} THEN 1 ELSE 0 END), 0) AS {k}"));
        var sql = $"SELECT COALESCE(SUM(CASE WHEN t.Missing = 0 THEN 1 ELSE 0 END), 0) AS TotalTracks, {columns} FROM Tracks t";

        using var conn = _factory.Open();
        var row = (IDictionary<string, object>)(await conn.QuerySingleAsync<dynamic>(new CommandDefinition(sql, cancellationToken: ct)));
        var counts = kinds.ToDictionary(k => k, k => (int)Convert.ToInt64(row[k.ToString()]));
        var total = (int)Convert.ToInt64(row["TotalTracks"]);
        return new HealthSummary(total, counts, await _roots.GetUnsupportedAsync(ct));
    }

    public async Task<IReadOnlyList<long>> QueryIssuesAsync(HealthIssueKind kind, int skip, int take, CancellationToken ct)
    {
        using var conn = _factory.Open();
        return (await conn.QueryAsync<long>(new CommandDefinition(
            $"SELECT t.Id FROM Tracks t WHERE {Predicate(kind)} ORDER BY t.Id LIMIT @take OFFSET @skip",
            new { skip = Math.Max(0, skip), take = Math.Clamp(take, 1, 10_000) }, cancellationToken: ct))).AsList();
    }
}
