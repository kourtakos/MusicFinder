using MusicFinder.Application.Services;
using MusicFinder.Infrastructure.Genres;

namespace MusicFinder.UnitTests;

/// <summary>Guards the curated starter taxonomy against typos that would only surface as silent mapping gaps.</summary>
public class StarterTaxonomyTests
{
    private static readonly TaxonomyFile File = GenreTaxonomySeeder.LoadStarter();

    private static IEnumerable<string> AllGenreNames() =>
        File.Genres.SelectMany(g => new[] { g.Name }.Concat(g.Children ?? []));

    [Fact]
    public void ContainsTheRequiredTopLevelCategories()
    {
        var top = File.Genres.Select(g => g.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var required in new[] { "Electronic", "House", "Disco", "Funk", "Pop", "Rock", "Hip-Hop", "R&B", "Soul", "Jazz", "Latin", "Reggae", "Classical", "Greek", "Folk", "World" })
            Assert.Contains(required, top);
    }

    [Fact]
    public void GenreNames_AreUniqueAfterNormalization()
    {
        var names = AllGenreNames().Select(TextNormalizer.Normalize).ToList();
        var duplicates = names.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(duplicates.Count == 0, "Duplicate genres: " + string.Join(", ", duplicates));
        Assert.DoesNotContain("", names);
    }

    [Fact]
    public void EveryAliasTarget_Exists_AndAliasesDoNotCollide()
    {
        var names = AllGenreNames().Select(TextNormalizer.Normalize).ToHashSet();
        var seen = new Dictionary<string, string>();

        foreach (var genre in File.Genres)
            foreach (var raw in genre.Aliases ?? [])
            {
                var colon = raw.LastIndexOf(':');
                if (colon > 0)
                    Assert.True(names.Contains(TextNormalizer.Normalize(raw[(colon + 1)..])), $"alias target missing in '{raw}'");

                var alias = TextNormalizer.Normalize(colon > 0 ? raw[..colon] : raw);
                Assert.NotEqual("", alias);
                if (seen.TryGetValue(alias, out var owner))
                    Assert.Fail($"alias '{alias}' appears under both '{owner}' and '{genre.Name}'");
                seen[alias] = genre.Name;
            }
    }

    [Fact]
    public void ContainsTheDefaultDjTags() =>
        Assert.Equal(["Warm Up", "Peak Time", "Closing", "Wedding", "Greek Party"], File.Tags);

    [Fact]
    public void GreekNamesNormalizeLikeUserInput() =>
        Assert.Equal(TextNormalizer.Normalize("Λαϊκά"), TextNormalizer.Normalize("ΛΑΪΚΑ"));
}
