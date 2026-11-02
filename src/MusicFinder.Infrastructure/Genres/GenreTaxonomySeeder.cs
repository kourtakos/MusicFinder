using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using MusicFinder.Application.Services;
using MusicFinder.Infrastructure.Persistence;

namespace MusicFinder.Infrastructure.Genres;

public sealed record TaxonomyGenre(string Name, List<string>? Children, List<string>? Aliases);

public sealed record TaxonomyFile(List<TaxonomyGenre> Genres, List<string> Tags);

/// <summary>
/// Seeds the curated starter taxonomy and the default DJ tags once. Done in C# (not SQL) so every
/// normalized name comes from the same <see cref="TextNormalizer"/> that is used when matching.
/// After the first run the user owns the data; deleted built-ins are not resurrected.
/// </summary>
public sealed class GenreTaxonomySeeder
{
    private const string SeedKey = "TaxonomySeedVersion";
    private const string SeedVersion = "1";
    private readonly SqliteWriteQueue _writes;

    public GenreTaxonomySeeder(SqliteWriteQueue writes) => _writes = writes;

    public static TaxonomyFile LoadStarter()
    {
        var asm = typeof(GenreTaxonomySeeder).Assembly;
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith("starter-taxonomy.json", StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(name)!;
        return JsonSerializer.Deserialize<TaxonomyFile>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException("starter-taxonomy.json is empty.");
    }

    public Task EnsureSeededAsync(CancellationToken ct = default) =>
        _writes.EnqueueAsync((conn, tx) =>
        {
            if (conn.ExecuteScalar<string?>("SELECT Value FROM AppState WHERE Key = @k", new { k = SeedKey }, tx) == SeedVersion)
                return;
            Seed(conn, tx, LoadStarter());
            conn.Execute("INSERT OR REPLACE INTO AppState(Key, Value) VALUES (@k, @v)", new { k = SeedKey, v = SeedVersion }, tx);
        }, ct);

    internal static void Seed(SqliteConnection conn, SqliteTransaction tx, TaxonomyFile file)
    {
        var ids = new Dictionary<string, long>(StringComparer.Ordinal); // normalized name -> id
        var order = 0;

        long Upsert(string name, long? parentId)
        {
            var norm = TextNormalizer.Normalize(name);
            if (ids.TryGetValue(norm, out var existing)) return existing;
            var id = conn.QuerySingle<long>(
                """
                INSERT INTO Genres(ParentId, Name, NormalizedName, SortOrder, IsBuiltIn) VALUES (@parentId, @name, @norm, @order, 1)
                ON CONFLICT(NormalizedName) DO UPDATE SET NormalizedName = NormalizedName
                RETURNING Id
                """, new { parentId, name, norm, order = order++ }, tx);
            ids[norm] = id;
            return id;
        }

        foreach (var parent in file.Genres)
        {
            var parentId = Upsert(parent.Name, null);
            foreach (var child in parent.Children ?? []) Upsert(child, parentId);
        }

        foreach (var parent in file.Genres)
        {
            var parentId = ids[TextNormalizer.Normalize(parent.Name)];
            foreach (var raw in parent.Aliases ?? [])
            {
                // "alias:Target" points at a specific sub-genre; a bare alias points at the parent.
                var colon = raw.LastIndexOf(':');
                var aliasText = colon > 0 ? raw[..colon] : raw;
                var targetId = colon > 0 && ids.TryGetValue(TextNormalizer.Normalize(raw[(colon + 1)..]), out var t) ? t : parentId;

                var alias = TextNormalizer.Normalize(aliasText);
                if (alias.Length == 0 || ids.ContainsKey(alias)) continue; // exact genre names already match
                conn.Execute("INSERT OR IGNORE INTO GenreAliases(AliasNormalized, GenreId) VALUES (@alias, @targetId)",
                    new { alias, targetId }, tx);
            }
        }

        var tagOrder = 1;
        foreach (var tag in file.Tags)
            conn.Execute("INSERT OR IGNORE INTO CustomTags(Name, SortOrder) VALUES (@tag, @o)", new { tag, o = tagOrder++ }, tx);
    }
}
