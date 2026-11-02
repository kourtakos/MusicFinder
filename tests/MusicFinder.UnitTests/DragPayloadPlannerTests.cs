using MusicFinder.Application.Dj;

namespace MusicFinder.UnitTests;

public class DragPayloadPlannerTests
{
    private static DragCandidate C(long id, string path, bool missing = false) => new(id, path, missing);
    private static readonly Func<string, bool> Everything = _ => true;

    [Fact]
    public void ExistingFiles_AreIncluded_InSelectionOrder()
    {
        var plan = DragPayloadPlanner.Plan([C(3, @"C:\m\c.mp3"), C(1, @"C:\m\a.mp3"), C(2, @"C:\m\b.mp3")], Everything);

        Assert.Equal([3L, 1L, 2L], plan.Included.Select(i => i.TrackId));
        Assert.Empty(plan.SkippedMissing);
        Assert.Equal(0, plan.Truncated);
        Assert.False(plan.IsEmpty);
    }

    [Fact]
    public void TracksTheIndexKnowsAreMissing_AreSkipped_WithoutTouchingTheDisk()
    {
        var probed = new List<string>();
        var plan = DragPayloadPlanner.Plan([C(1, @"C:\m\a.mp3"), C(2, @"C:\m\gone.mp3", missing: true)],
            p => { probed.Add(p); return true; });

        Assert.Equal([1L], plan.Included.Select(i => i.TrackId));
        Assert.Equal([2L], plan.SkippedMissing.Select(i => i.TrackId));
        Assert.DoesNotContain(@"C:\m\gone.mp3", probed);
    }

    [Fact]
    public void AFileThatVanishedSinceTheLastScan_IsCaughtByTheDiskCheck_ForSmallSelections()
    {
        var plan = DragPayloadPlanner.Plan([C(1, @"C:\m\a.mp3"), C(2, @"C:\m\b.mp3")], p => p.EndsWith("a.mp3"));

        Assert.Equal([1L], plan.Included.Select(i => i.TrackId));
        Assert.Equal([2L], plan.SkippedMissing.Select(i => i.TrackId));
    }

    [Fact]
    public void LargeSelections_TrustTheIndex_InsteadOfProbingEveryFile()
    {
        var probes = 0;
        var many = Enumerable.Range(0, 60).Select(i => C(i, $@"\\nas\music\{i}.mp3")).ToList();

        var plan = DragPayloadPlanner.Plan(many, _ => { probes++; return false; }); // would reject everything if it were asked

        Assert.Equal(0, probes);               // a slow network share is never stat-ed per file on the UI thread
        Assert.Equal(60, plan.Included.Count);
    }

    [Fact]
    public void ADiskCheckThatThrows_CountsAsMissing_NeverCrashesTheDrag()
    {
        var plan = DragPayloadPlanner.Plan([C(1, @"C:\m\a.mp3"), C(2, @"C:\m\b.mp3")],
            p => p.EndsWith("a.mp3") ? true : throw new UnauthorizedAccessException());

        Assert.Equal([1L], plan.Included.Select(i => i.TrackId));
        Assert.Equal([2L], plan.SkippedMissing.Select(i => i.TrackId));
    }

    [Fact]
    public void TheSameFileSelectedTwice_IsDraggedOnce()
    {
        var plan = DragPayloadPlanner.Plan([C(1, @"C:\m\a.mp3"), C(2, @"C:\m\a.mp3"), C(3, @"C:\m\b.mp3")], Everything);
        Assert.Equal([1L, 3L], plan.Included.Select(i => i.TrackId));
    }

    [Fact]
    public void Paths_AreNeverRewritten_GreekUncAndVeryLongPathsGoThroughVerbatim()
    {
        var greek = @"C:\Μουσική\Χάρις Αλεξίου - Σ' αγαπώ.mp3";
        var unc = @"\\server\share\Disco\Chic - Le Freak.mp3";
        var longPath = @"C:\" + string.Join('\\', Enumerable.Repeat(new string('x', 40), 8)) + @"\song.mp3"; // > 260 characters
        var extended = @"\\?\C:\Music\song.mp3";
        Assert.True(longPath.Length > 260);

        var plan = DragPayloadPlanner.Plan([C(1, greek), C(2, unc), C(3, longPath), C(4, extended)], Everything);

        Assert.Equal([greek, unc, longPath, extended], plan.Included.Select(i => i.Path));
    }

    [Fact]
    public void PathsDifferingOnlyByCase_AreKeptApart_BecauseTheOriginalCasingIsPreserved()
    {
        var plan = DragPayloadPlanner.Plan([C(1, @"C:\m\Song.mp3"), C(2, @"C:\m\song.mp3")], Everything);
        Assert.Equal(2, plan.Included.Count);
    }

    [Fact]
    public void TheCap_KeepsTheFirstFiles_AndReportsHowManyWereLeftOut()
    {
        var many = Enumerable.Range(0, 520).Select(i => C(i, $@"C:\m\{i}.mp3")).ToList();

        var plan = DragPayloadPlanner.Plan(many, Everything);

        Assert.Equal(500, plan.Included.Count);
        Assert.Equal(20, plan.Truncated);
        Assert.Equal(0, plan.Included[0].TrackId);
        Assert.Equal(499, plan.Included[^1].TrackId);
    }

    [Fact]
    public void EverythingMissing_YieldsAnEmptyPlan()
    {
        var plan = DragPayloadPlanner.Plan([C(1, @"C:\a.mp3", missing: true), C(2, @"C:\b.mp3", missing: true)], Everything);

        Assert.True(plan.IsEmpty);
        Assert.Equal(2, plan.SkippedMissing.Count);
    }

    [Fact]
    public void EmptySelection_AndBlankPaths_AreHandled()
    {
        Assert.True(DragPayloadPlanner.Plan([], Everything).IsEmpty);
        Assert.True(DragPayloadPlanner.Plan([C(1, ""), C(2, "   ")], Everything).IsEmpty);
    }
}
