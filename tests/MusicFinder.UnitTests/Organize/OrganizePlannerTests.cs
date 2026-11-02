using MusicFinder.Application.Organize;
using MusicFinder.Domain.Models;

namespace MusicFinder.UnitTests.Organize;

public class OrganizePlannerTests
{
    private sealed class Probe(params string[] existing) : IFileProbe
    {
        private readonly HashSet<string> _files = new(existing, StringComparer.OrdinalIgnoreCase);
        public bool Exists(string path) => _files.Contains(path);
    }

    private static RenameSource Src(long id, string path, string? artist = "Madonna", string? title = "Frozen", string? genre = "Pop", string? root = @"C:\M") =>
        new(id, path, artist, title, "Ray of Light", null, null, null, genre, null, null, root);

    private static readonly RenameOptions ByGenre = new("{Genre}/{Artist}/{Artist} - {Title}", MoveIntoFolders: true);

    [Fact]
    public void TheFileMovesIntoFoldersBelowItsLibraryFolder()
    {
        var item = Assert.Single(FileOperationPlanner.PlanRename([Src(1, @"C:\M\Inbox\x.mp3")], ByGenre, new Probe(@"C:\M\Inbox\x.mp3")));

        Assert.Equal(PlanStatus.Rename, item.Status);
        Assert.Equal(@"C:\M\Pop\Madonna\Madonna - Frozen.mp3", item.NewPath);
    }

    [Fact]
    public void ValuesCannotInjectFolders_AndDotsCannotEscapeTheLibraryFolder()
    {
        var p = new Probe(@"C:\M\x.mp3");
        var a = Assert.Single(FileOperationPlanner.PlanRename([Src(1, @"C:\M\x.mp3", artist: "AC/DC", genre: "R&B / Soul")], ByGenre, p));
        Assert.Equal(@"C:\M\R&B _ Soul\AC_DC\AC_DC - Frozen.mp3", a.NewPath);

        var b = Assert.Single(FileOperationPlanner.PlanRename([Src(1, @"C:\M\x.mp3")], new RenameOptions("../../{Title}", MoveIntoFolders: true), p));
        Assert.StartsWith(@"C:\M\", b.NewPath);
        Assert.DoesNotContain("..", b.NewPath);
    }

    [Fact]
    public void AMissingGenre_SkipsTheFile_AndAFileAlreadyInPlaceIsUnchanged()
    {
        var p = new Probe(@"C:\M\x.mp3", @"C:\M\Pop\Madonna\Madonna - Frozen.mp3");
        var plan = FileOperationPlanner.PlanRename(
            [Src(1, @"C:\M\x.mp3", genre: null), Src(2, @"C:\M\Pop\Madonna\Madonna - Frozen.mp3")], ByGenre, p);

        Assert.Equal(PlanStatus.Skipped, plan[0].Status);
        Assert.Contains("{Genre}", plan[0].Reason);
        Assert.Equal(PlanStatus.Unchanged, plan[1].Status);
    }

    [Fact]
    public void ASourceWithoutALibraryFolder_IsSkipped() =>
        Assert.Equal(PlanStatus.Skipped,
            Assert.Single(FileOperationPlanner.PlanRename([Src(1, @"C:\M\x.mp3", root: null)], ByGenre, new Probe(@"C:\M\x.mp3"))).Status);

    [Fact]
    public void TwoFilesForTheSameDestination_AndTheSidecars_FollowTheSameRules()
    {
        var p = new Probe(@"C:\M\a.mp3", @"C:\M\b.mp3", @"C:\M\a.lrc");
        var plan = FileOperationPlanner.PlanRename([Src(1, @"C:\M\a.mp3"), Src(2, @"C:\M\b.mp3")], ByGenre, p);

        Assert.Equal(PlanStatus.Rename, plan[0].Status);
        Assert.Equal(@"C:\M\Pop\Madonna\Madonna - Frozen.lrc", Assert.Single(plan[0].Sidecars).NewPath);
        Assert.Equal(PlanStatus.Skipped, plan[1].Status);
    }

    [Fact]
    public void WithoutTheOption_ASlashInTheTemplateStaysInsideTheFileName()
    {
        var item = Assert.Single(FileOperationPlanner.PlanRename([Src(1, @"C:\M\x.mp3")], new RenameOptions("{Genre}/{Title}"), new Probe(@"C:\M\x.mp3")));
        Assert.Equal(@"C:\M\Pop_Frozen.mp3", item.NewPath);
    }
}
