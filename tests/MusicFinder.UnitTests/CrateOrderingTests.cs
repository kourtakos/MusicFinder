using MusicFinder.Application.Crates;
using MusicFinder.Domain.Models;

namespace MusicFinder.UnitTests;

public class CrateOrderingTests
{
    private static SearchResultRow Row(long id, double? bpm, string? key, string title = "T", string? artist = "A", long? durationMs = 200_000,
        bool missing = false) =>
        new(id, 1, $@"C:\Music\{id}.mp3", $"{id}.mp3", "mp3", 1000, title, artist, null, null, null, durationMs, null, bpm, key, missing);

    [Fact]
    public void ByBpm_SortsSlowestFirst_AndLeavesUnknownBpmAtTheEndInTheirOrder()
    {
        var rows = new[] { Row(1, 128, "8A"), Row(2, null, null), Row(3, 100, "8A"), Row(4, 0, null), Row(5, 100, "9A") };
        Assert.Equal([3, 5, 1, 2, 4], CrateOrdering.ByBpm(rows));
    }

    [Fact]
    public void Harmonic_FollowsTheWheel_AndBreaksTiesByTempoThenId()
    {
        // From 8A (slowest): 8B is relative (1), 9A is a neighbour (1) and closer in tempo than 8B; 3A is far.
        var rows = new[]
        {
            Row(1, 120, "8A"), Row(2, 126, "8B"), Row(3, 122, "9A"), Row(4, 124, "3A"),
        };
        var order = CrateOrdering.Harmonic(rows);
        Assert.Equal(1, order[0]);
        Assert.Equal(3, order[1]);   // 9A: distance 1, tempo +2
        Assert.Equal(4, order.Count);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, order.OrderBy(x => x));
    }

    [Fact]
    public void Harmonic_IsDeterministic_AndTreatsTwelveAndOneAsNeighbours()
    {
        var rows = new[] { Row(7, 120, "12A"), Row(3, 120, "1A"), Row(9, 120, "6A") };
        var a = CrateOrdering.Harmonic(rows);
        var b = CrateOrdering.Harmonic(rows.Reverse().ToList());
        Assert.Equal(a, b);
        Assert.Equal([3, 7, 9], a);  // equal tempo: lowest id starts, then the wheel neighbour 12A, then the far 6A
    }

    [Fact]
    public void Harmonic_NeverInventsAKeyOrBpm_ThoseTracksGoLastInTheirOrder()
    {
        var rows = new[] { Row(1, null, "8A"), Row(2, 120, "8A"), Row(3, 121, "not a key"), Row(4, 122, "8B") };
        var order = CrateOrdering.Harmonic(rows);
        Assert.Equal([2, 4, 1, 3], order);
    }

    [Fact]
    public void Harmonic_WithNothingUsable_KeepsTheCurrentOrder()
    {
        var rows = new[] { Row(5, null, null), Row(2, null, "8A") };
        Assert.Equal([5, 2], CrateOrdering.Harmonic(rows));
    }

    [Fact]
    public void M3u_ListsPathsWithDurationAndName_SkipsMissingFiles()
    {
        var rows = new[]
        {
            Row(1, 120, "8A", title: "Le Freak", artist: "Chic", durationMs: 195_400),
            Row(2, 120, "8A", title: "No artist", artist: null, durationMs: null),
            Row(3, 120, "8A", title: "Gone", missing: true),
            Row(4, 120, "8A", title: "Two\nlines", artist: "X"),
        };
        var (text, skipped) = M3uWriter.Build(rows);

        Assert.Equal(1, skipped);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("#EXTM3U", lines[0]);
        Assert.Equal("#EXTINF:195,Chic - Le Freak", lines[1]);
        Assert.Equal(@"C:\Music\1.mp3", lines[2]);
        Assert.Equal("#EXTINF:-1,No artist", lines[3]);
        Assert.Equal("#EXTINF:200,X - Two lines", lines[5]);
        Assert.DoesNotContain("Gone", text);
    }

    [Fact]
    public void M3u_WritesUtf8WithoutABom_ForGreekNames()
    {
        var path = Path.Combine(Path.GetTempPath(), "mf-m3u-" + Guid.NewGuid().ToString("N") + ".m3u8");
        try
        {
            M3uWriter.Write(path, [Row(1, 120, "8A", title: "Γεια σου", artist: "Χάρις Αλεξίου")], out var skipped);
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(0, skipped);
            Assert.NotEqual(0xEF, bytes[0]);                       // no BOM
            Assert.Contains("Χάρις Αλεξίου - Γεια σου", System.Text.Encoding.UTF8.GetString(bytes));
        }
        finally { File.Delete(path); }
    }
}
