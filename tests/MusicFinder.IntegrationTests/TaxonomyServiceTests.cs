using Dapper;
using MusicFinder.Domain.Enums;
using MusicFinder.Domain.Models;

namespace MusicFinder.IntegrationTests;

public class TaxonomyServiceTests
{
    private sealed record Env(TestDb Db, long Folder);

    private static async Task<Env> CreateAsync()
    {
        var db = new TestDb();
        await db.SeedTaxonomyAsync();
        var root = await db.Roots.AddAsync(@"C:\Music");
        var folder = await db.Folders.UpsertAsync(root.Id, null, @"C:\Music", "Music");
        return new Env(db, folder);
    }

    private static async Task<long> AddTrackAsync(Env e, string name, string? genre)
    {
        await e.Db.Tracks.UpsertBatchAsync([TestDb.MakeTrack(e.Folder, $@"C:\Music\{name}.mp3", name, "A", genre)]);
        await e.Db.GenreJob.RunAsync();
        return (await e.Db.Tracks.GetIndexedFilesAsync((await e.Db.Roots.GetAllAsync())[0].Id)).Single(f => f.FullPath.EndsWith(name + ".mp3")).Id;
    }

    private static async Task<long> GenreId(Env e, string name) => (await e.Db.Genres.GetAllAsync()).Single(g => g.Name == name).Id;

    private static async Task<List<string>> NamesOf(Env e, long trackId)
    {
        var all = (await e.Db.Genres.GetAllAsync()).ToDictionary(g => g.Id, g => g.Name);
        return (await e.Db.TrackGenres.GetForTracksAsync([trackId])).Select(a => all[a.GenreId]).OrderBy(n => n).ToList();
    }

    [Fact]
    public async Task Taxonomy_IsATree_WithDistinctSubtreeCounts()
    {
        var e = await CreateAsync();
        await using var _ = e.Db;
        await AddTrackAsync(e, "both", "Disco;Nu Disco");   // in parent and child: counts once for Disco
        await AddTrackAsync(e, "child", "Nu Disco");
        await AddTrackAsync(e, "plain", "Disco");

        var tree = await e.Db.GenreService.GetTaxonomyAsync();
        Assert.Equal(16, tree.Count);
        var disco = tree.Single(n => n.Genre.Name == "Disco");
        Assert.Equal(3, disco.TrackCount);                                         // not 5
        Assert.Equal(2, disco.Children.Single(c => c.Genre.Name == "Nu Disco").TrackCount);
        Assert.Equal(0, tree.Single(n => n.Genre.Name == "Jazz").TrackCount);
    }

    [Fact]
    public async Task AddGenre_ValidatesNames_AndRemapsExistingTracks()
    {
        var e = await CreateAsync();
        await using var _ = e.Db;
        var track = await AddTrackAsync(e, "a", "Vaporwave");
        Assert.Empty(await NamesOf(e, track));

        await Assert.ThrowsAsync<TaxonomyException>(() => e.Db.GenreService.AddGenreAsync("   ", null));
        await Assert.ThrowsAsync<TaxonomyException>(() => e.Db.GenreService.AddGenreAsync("!!!", null));
        await Assert.ThrowsAsync<TaxonomyException>(() => e.Db.GenreService.AddGenreAsync("nu-disco", null));       // normalizes to an existing genre
        await Assert.ThrowsAsync<TaxonomyException>(() => e.Db.GenreService.AddGenreAsync("Funky", null));          // existing alias
        await Assert.ThrowsAsync<TaxonomyException>(() => e.Db.GenreService.AddGenreAsync(new string('x', 81), null));
        await Assert.ThrowsAsync<TaxonomyException>(() => e.Db.GenreService.AddGenreAsync("Orphan", 99999));

        var raised = 0;
        e.Db.GenreService.TaxonomyChanged += (_, _) => raised++;
        var id = await e.Db.GenreService.AddGenreAsync("  Vaporwave ", await GenreId(e, "Electronic"));

        Assert.Equal(1, raised);
        Assert.Equal(["Vaporwave"], await NamesOf(e, track)); // existing tracks pick up the new genre without rescanning
        Assert.Equal(id, (await e.Db.Genres.GetAllAsync()).Single(g => g.Name == "Vaporwave").Id);
        Assert.False((await e.Db.Genres.GetAllAsync()).Single(g => g.Id == id).IsBuiltIn);
    }

    [Fact]
    public async Task Rename_KeepsMappings_AndRejectsDuplicates()
    {
        var e = await CreateAsync();
        await using var _ = e.Db;
        var track = await AddTrackAsync(e, "a", "Laiko");

        await e.Db.GenreService.RenameGenreAsync(await GenreId(e, "Laiko"), "Λαϊκό");
        Assert.Equal(["Λαϊκό"], await NamesOf(e, track));

        var laiko = await GenreId(e, "Λαϊκό");
        await Assert.ThrowsAsync<TaxonomyException>(() => e.Db.GenreService.RenameGenreAsync(laiko, "Entexno"));
        await e.Db.GenreService.RenameGenreAsync(laiko, "ΛΑΪΚΟ"); // same genre, different case: allowed
        Assert.DoesNotContain(await e.Db.Genres.GetAliasesAsync(), a => a.AliasNormalized == "λαικο"); // redundant alias dropped
        Assert.Equal(["ΛΑΪΚΟ"], await NamesOf(e, track));
    }

    [Fact]
    public async Task Move_RejectsCycles_AndChangesFiltering()
    {
        var e = await CreateAsync();
        await using var _ = e.Db;
        var disco = await GenreId(e, "Disco");
        var nu = await GenreId(e, "Nu Disco");
        var jazz = await GenreId(e, "Jazz");

        await Assert.ThrowsAsync<TaxonomyException>(() => e.Db.GenreService.MoveGenreAsync(disco, nu));    // into own child
        await Assert.ThrowsAsync<TaxonomyException>(() => e.Db.GenreService.MoveGenreAsync(disco, disco)); // into itself
        await Assert.ThrowsAsync<TaxonomyException>(() => e.Db.GenreService.MoveGenreAsync(nu, 424242));   // missing parent

        await e.Db.GenreService.MoveGenreAsync(nu, jazz);
        Assert.Equal(jazz, (await e.Db.Genres.GetAllAsync()).Single(g => g.Id == nu).ParentId);
        await e.Db.GenreService.MoveGenreAsync(nu, null); // promote to top level
        Assert.Null((await e.Db.Genres.GetAllAsync()).Single(g => g.Id == nu).ParentId);
    }

    [Fact]
    public async Task Delete_RejectsGenresWithChildren_AndCleansAliasesAndAssignments()
    {
        var e = await CreateAsync();
        await using var _ = e.Db;
        var track = await AddTrackAsync(e, "a", "Skyladiko;Funk");

        var greek = await GenreId(e, "Greek");
        await Assert.ThrowsAsync<TaxonomyException>(() => e.Db.GenreService.DeleteGenreAsync(greek));

        var sky = await GenreId(e, "Skyladiko");
        await e.Db.GenreService.AddAliasAsync("skylas", sky);
        await e.Db.GenreService.DeleteGenreAsync(sky);

        Assert.DoesNotContain(await e.Db.Genres.GetAllAsync(), g => g.Id == sky);
        Assert.DoesNotContain(await e.Db.Genres.GetAliasesAsync(), a => a.GenreId == sky);
        Assert.Equal(["Funk"], await NamesOf(e, track));
        Assert.Equal("Skyladiko;Funk", (await e.Db.Tracks.GetAsync(track))!.GenreRaw); // the file's text is never edited

        // "Skyladiko" is unmapped again and now shows up for the user to map elsewhere.
        Assert.Contains(await e.Db.GenreService.GetUnmappedAsync(), u => u.Value == "Skyladiko");
    }

    [Fact]
    public async Task Aliases_AreValidated_AndTriggerRemap()
    {
        var e = await CreateAsync();
        await using var _ = e.Db;
        var track = await AddTrackAsync(e, "a", "Synth Wave");
        var synthpop = await GenreId(e, "Synthpop");
        Assert.Empty(await NamesOf(e, track));

        await Assert.ThrowsAsync<TaxonomyException>(() => e.Db.GenreService.AddAliasAsync(" ?! ", synthpop));
        await Assert.ThrowsAsync<TaxonomyException>(() => e.Db.GenreService.AddAliasAsync("disco", synthpop));       // is a genre name
        await Assert.ThrowsAsync<TaxonomyException>(() => e.Db.GenreService.AddAliasAsync("funky", synthpop));       // alias of Funk
        await Assert.ThrowsAsync<TaxonomyException>(() => e.Db.GenreService.AddAliasAsync("x", 31337));

        await e.Db.GenreService.AddAliasAsync("Synth-Wave", synthpop);
        Assert.Equal(["Synthpop"], await NamesOf(e, track));
        await Assert.ThrowsAsync<TaxonomyException>(() => e.Db.GenreService.AddAliasAsync("synth wave", synthpop)); // duplicate

        await e.Db.GenreService.RemoveAliasAsync("SYNTH WAVE");
        Assert.Empty(await NamesOf(e, track));
    }

    [Fact]
    public async Task Unmapped_AreAggregatedAcrossTracksAndSpellings()
    {
        var e = await CreateAsync();
        await using var _ = e.Db;
        await AddTrackAsync(e, "a", "Vaporwave;Disco");
        await AddTrackAsync(e, "b", "vaporwave");
        await AddTrackAsync(e, "c", "Seapunk");
        await AddTrackAsync(e, "d", "Disco");

        var unmapped = await e.Db.GenreService.GetUnmappedAsync();
        Assert.Equal(2, unmapped.Count);
        Assert.Equal(2, unmapped[0].TrackCount);                          // both spellings counted together
        Assert.Equal("vaporwave", unmapped[0].Value.ToLowerInvariant());
        Assert.Equal(("Seapunk", 1), (unmapped[1].Value, unmapped[1].TrackCount));
    }

    [Fact]
    public async Task UserGenres_AreIndependentOfEmbeddedMapping()
    {
        var e = await CreateAsync();
        await using var _ = e.Db;
        var a = await AddTrackAsync(e, "a", "Vaporwave");
        var b = await AddTrackAsync(e, "b", "Disco");
        var funk = await GenreId(e, "Funk");

        var raised = 0;
        e.Db.GenreService.TaxonomyChanged += (_, _) => raised++;
        await e.Db.GenreService.SetUserGenreAsync([a, b], funk, assigned: true);
        await e.Db.GenreService.SetUserGenreAsync([a, b], funk, assigned: true); // idempotent
        Assert.Equal(2, raised);

        var assignments = await e.Db.GenreService.GetAssignmentsAsync([a, b]);
        Assert.Contains((a, funk, GenreSource.User), assignments);
        Assert.Equal(2, assignments.Count(x => x.GenreId == funk));

        // A taxonomy edit remaps embedded rows but leaves the user's choice alone.
        await e.Db.GenreService.AddAliasAsync("vaporwave", await GenreId(e, "Electronic"));
        Assert.Equal(["Electronic", "Funk"], await NamesOf(e, a));

        await e.Db.GenreService.SetUserGenreAsync([a], funk, assigned: false);
        Assert.Equal(["Electronic"], await NamesOf(e, a));
        Assert.Equal(["Disco", "Funk"], await NamesOf(e, b)); // untouched
    }

    [Fact]
    public async Task DjTags_CrudAssignmentsAndCounts()
    {
        var e = await CreateAsync();
        await using var _ = e.Db;
        var a = await AddTrackAsync(e, "a", null);
        var b = await AddTrackAsync(e, "b", null);
        var tags = e.Db.TagService;

        var raised = 0;
        tags.TagsChanged += (_, _) => raised++;

        var warm = (await tags.GetAllAsync()).Single(t => t.Tag.Name == "Warm Up").Tag.Id;
        await tags.AssignAsync([a, b], warm);
        await tags.AssignAsync([a, b], warm); // idempotent
        Assert.Equal(2, (await tags.GetAllAsync()).Single(t => t.Tag.Id == warm).TrackCount);

        var assignments = await tags.GetAssignmentsAsync([a, b]);
        Assert.Equal([warm], assignments[a]);

        await tags.UnassignAsync([a], warm);
        Assert.False((await tags.GetAssignmentsAsync([a])).ContainsKey(a));
        Assert.Equal(1, (await tags.GetAllAsync()).Single(t => t.Tag.Id == warm).TrackCount);

        var custom = await tags.AddAsync("  Sunset Set ");
        await Assert.ThrowsAsync<TaxonomyException>(() => tags.AddAsync("sunset  set"));
        await Assert.ThrowsAsync<TaxonomyException>(() => tags.AddAsync("  "));
        await Assert.ThrowsAsync<TaxonomyException>(() => tags.RenameAsync(custom, "warm up"));
        await tags.RenameAsync(custom, "Sunset");
        Assert.Contains((await tags.GetAllAsync()), t => t.Tag.Name == "Sunset");

        await tags.AssignAsync([a], custom);
        await tags.DeleteAsync(custom);
        Assert.DoesNotContain((await tags.GetAllAsync()), t => t.Tag.Id == custom);
        Assert.False((await tags.GetAssignmentsAsync([a])).ContainsKey(a)); // assignments went with it
        Assert.Equal(7, raised); // 2 assigns + unassign + add + rename + assign + delete; rejected edits raise nothing
    }
}
