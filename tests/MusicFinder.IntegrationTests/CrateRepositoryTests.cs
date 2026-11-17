using Dapper;

namespace MusicFinder.IntegrationTests;

public class CrateRepositoryTests
{
    private sealed record Env(TestDb Db, long A, long B, long C);

    private static async Task<Env> CreateAsync()
    {
        var db = new TestDb();
        var root = await db.Roots.AddAsync(@"C:\Music");
        var folder = await db.Folders.UpsertAsync(root.Id, null, @"C:\Music", "Music");
        await db.Tracks.UpsertBatchAsync(
        [
            TestDb.MakeTrack(folder, @"C:\Music\a.mp3", "Alpha", "Artist A"),
            TestDb.MakeTrack(folder, @"C:\Music\b.mp3", "Bravo", "Artist B"),
            TestDb.MakeTrack(folder, @"C:\Music\c.mp3", "Charlie", "Artist C"),
        ]);
        var files = await db.Tracks.GetIndexedFilesAsync(root.Id);
        long Id(string n) => files.Single(f => f.FullPath.EndsWith(n)).Id;
        return new Env(db, Id("a.mp3"), Id("b.mp3"), Id("c.mp3"));
    }

    [Fact]
    public async Task Create_RejectsADuplicateNameInAnyCase_AndListsByName()
    {
        var e = await CreateAsync();
        await using var _ = e.Db;
        var warm = await e.Db.Crates.CreateAsync("Warm Up");
        Assert.NotNull(warm);
        Assert.Null(await e.Db.Crates.CreateAsync("warm up"));
        await e.Db.Crates.CreateAsync("Closing");

        Assert.Equal(["Closing", "Warm Up"], (await e.Db.Crates.GetAllAsync()).Select(c => c.Name));
    }

    [Fact]
    public async Task Rename_ReturnsFalseOnClash_AndTrueOtherwise()
    {
        var e = await CreateAsync();
        await using var _ = e.Db;
        var one = (await e.Db.Crates.CreateAsync("One"))!.Value;
        await e.Db.Crates.CreateAsync("Two");

        Assert.False(await e.Db.Crates.RenameAsync(one, "TWO"));
        Assert.True(await e.Db.Crates.RenameAsync(one, "Three"));
        Assert.Contains(await e.Db.Crates.GetAllAsync(), c => c.Name == "Three");
    }

    [Fact]
    public async Task AddTracks_AppendsInOrder_SkipsDuplicatesAndUnknownTracks()
    {
        var e = await CreateAsync();
        await using var _ = e.Db;
        var crate = (await e.Db.Crates.CreateAsync("Set"))!.Value;

        Assert.Equal(2, await e.Db.Crates.AddTracksAsync(crate, [e.C, e.A]));
        // B is new, A is already there, 99999 does not exist, and C repeated in the same call counts once.
        Assert.Equal(1, await e.Db.Crates.AddTracksAsync(crate, [e.A, e.B, 99999, e.B]));

        Assert.Equal([e.C, e.A, e.B], await e.Db.Crates.GetTrackIdsAsync(crate));
        Assert.Equal(3, (await e.Db.Crates.GetAllAsync()).Single().TrackCount);
    }

    [Fact]
    public async Task Remove_AndReorder_KeepTheRestInOrder()
    {
        var e = await CreateAsync();
        await using var _ = e.Db;
        var crate = (await e.Db.Crates.CreateAsync("Set"))!.Value;
        await e.Db.Crates.AddTracksAsync(crate, [e.A, e.B, e.C]);

        await e.Db.Crates.ReorderAsync(crate, [e.C, e.A]);   // B is not listed: it follows
        Assert.Equal([e.C, e.A, e.B], await e.Db.Crates.GetTrackIdsAsync(crate));

        await e.Db.Crates.RemoveTracksAsync(crate, [e.A]);
        Assert.Equal([e.C, e.B], await e.Db.Crates.GetTrackIdsAsync(crate));

        // Adding after a removal still goes to the end.
        await e.Db.Crates.AddTracksAsync(crate, [e.A]);
        Assert.Equal([e.C, e.B, e.A], await e.Db.Crates.GetTrackIdsAsync(crate));
    }

    [Fact]
    public async Task OneTrackCanBeInSeveralCrates_AndDeletingACrateLeavesTracksAlone()
    {
        var e = await CreateAsync();
        await using var _ = e.Db;
        var one = (await e.Db.Crates.CreateAsync("One"))!.Value;
        var two = (await e.Db.Crates.CreateAsync("Two"))!.Value;
        await e.Db.Crates.AddTracksAsync(one, [e.A, e.B]);
        await e.Db.Crates.AddTracksAsync(two, [e.A]);

        await e.Db.Crates.DeleteAsync(one);

        Assert.Equal([e.A], await e.Db.Crates.GetTrackIdsAsync(two));
        Assert.Empty(await e.Db.Crates.GetTrackIdsAsync(one));
        using var conn = e.Db.Factory.Open();
        Assert.Equal(3, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Tracks"));
    }

    [Fact]
    public async Task MissingTracksStayInTheCrateButAreNotCounted()
    {
        var e = await CreateAsync();
        await using var _ = e.Db;
        var crate = (await e.Db.Crates.CreateAsync("Set"))!.Value;
        await e.Db.Crates.AddTracksAsync(crate, [e.A, e.B]);

        using (var conn = e.Db.Factory.Open())
            await conn.ExecuteAsync("UPDATE Tracks SET Missing = 1 WHERE Id = @id", new { id = e.B });

        Assert.Equal([e.A, e.B], await e.Db.Crates.GetTrackIdsAsync(crate));
        Assert.Equal(1, (await e.Db.Crates.GetAllAsync()).Single().TrackCount);
    }
}
