namespace MusicFinder.IntegrationTests;

public class SavedSearchRepositoryTests
{
    [Fact]
    public async Task AddListDelete_NamesAreUniqueInAnyCase_AndListedByName()
    {
        await using var db = new TestDb();
        Assert.NotNull(await db.SavedSearches.AddAsync("Disco 120", "{\"Version\":1}"));
        Assert.Null(await db.SavedSearches.AddAsync("disco 120", "{\"Version\":1}"));
        var other = (await db.SavedSearches.AddAsync("Apex", "{\"Version\":1,\"Text\":\"x\"}"))!.Value;

        var all = await db.SavedSearches.GetAllAsync();
        Assert.Equal(["Apex", "Disco 120"], all.Select(s => s.Name));
        Assert.Equal("{\"Version\":1,\"Text\":\"x\"}", all[0].StateJson);

        await db.SavedSearches.DeleteAsync(other);
        Assert.Equal(["Disco 120"], (await db.SavedSearches.GetAllAsync()).Select(s => s.Name));
    }
}
