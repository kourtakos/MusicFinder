using Dapper;
using MusicFinder.Domain.Entities;
using MusicFinder.Domain.Enums;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure.Search;

namespace MusicFinder.IntegrationTests;

public class HealthFilterTests
{
    private static async Task<(TestDb Db, SqliteSearchService Search)> CreateAsync()
    {
        var db = new TestDb();
        var root = await db.Roots.AddAsync(@"C:\Music");
        var folder = await db.Folders.UpsertAsync(root.Id, null, @"C:\Music", "Music");

        Track T(string name, IssueFlags flags, int? kbps, string? codec, string? art = null, string? readError = null)
        {
            var t = TestDb.MakeTrack(folder, $@"C:\Music\{name}.mp3", name, "Artist", "Disco");
            t.IssueFlags = (int)flags; t.BitrateKbps = kbps; t.Codec = codec; t.ArtworkCacheKey = art; t.ReadError = readError;
            return t;
        }

        await db.Tracks.UpsertBatchAsync(
        [
            T("clean", IssueFlags.None, 320, "MPEG Version 1 Audio, Layer 3", "abc"),
            T("lowrate", IssueFlags.LowBitrate, 96, "MPEG Version 1 Audio, Layer 3"),
            T("garbled", IssueFlags.Mojibake, 256, "MPEG Version 1 Audio, Layer 3", "abc"),
            T("broken", IssueFlags.Unreadable, null, null, null, "boom"),
            T("flac_in_mp3", IssueFlags.CodecMismatch | IssueFlags.OddSampleRate, 900, "FLAC", "abc")
        ]);
        return (db, new SqliteSearchService(db.Factory));
    }

    private static async Task<List<string>> Titles(SqliteSearchService s, string text) =>
        (await s.SearchAsync(new SearchQuery { Text = text }, default)).Items.Select(i => i.Title).OrderBy(t => t).ToList();

    [Fact]
    public async Task EveryIssueFilter_FindsExactlyItsTracks()
    {
        var (db, s) = await CreateAsync();
        await using var _ = db;
        Assert.Equal(["lowrate"], await Titles(s, "issue:lowbitrate"));
        Assert.Equal(["garbled"], await Titles(s, "issue:mojibake"));
        Assert.Equal(["broken"], await Titles(s, "issue:error"));
        Assert.Equal(["flac_in_mp3"], await Titles(s, "issue:mismatch"));
        Assert.Equal(["flac_in_mp3"], await Titles(s, "issue:samplerate"));
        Assert.Equal(["broken", "flac_in_mp3", "garbled", "lowrate"], await Titles(s, "issue:any"));
        Assert.Empty(await Titles(s, "issue:size"));
    }

    [Fact]
    public async Task MissingArtwork_FindsTracksWithoutACover()
    {
        var (db, s) = await CreateAsync();
        await using var _ = db;
        Assert.Equal(["broken", "lowrate"], await Titles(s, "missing:art"));
        Assert.Equal(["broken", "lowrate"], await Titles(s, "missing:cover"));
    }

    [Fact]
    public async Task BitrateAndCodec_Filter()
    {
        var (db, s) = await CreateAsync();
        await using var _ = db;
        Assert.Equal(["lowrate"], await Titles(s, "bitrate:0-127"));
        Assert.Equal(["clean"], await Titles(s, "bitrate:320"));
        Assert.Equal(["flac_in_mp3"], await Titles(s, "codec:flac"));
        Assert.Equal(["clean", "garbled", "lowrate"], await Titles(s, "codec:mpeg"));
    }

    [Fact]
    public async Task FiltersCombine_AndTheResultRowsCarryTheQualityFacts()
    {
        var (db, s) = await CreateAsync();
        await using var _ = db;
        var page = await s.SearchAsync(new SearchQuery { Text = "issue:any missing:art" }, default);

        Assert.Equal(["broken", "lowrate"], page.Items.Select(i => i.Title).OrderBy(t => t));
        var broken = page.Items.Single(i => i.Title == "broken");
        Assert.Equal("boom", broken.ReadError);
        Assert.Equal((int)IssueFlags.Unreadable, broken.IssueFlags);
        Assert.Contains("MPEG", page.Items.Single(i => i.Title == "lowrate").Codec);
    }
}
