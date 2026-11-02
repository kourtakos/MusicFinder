using MusicFinder.Application.Organize;
using MusicFinder.Domain.Models;

namespace MusicFinder.UnitTests.Organize;

public class PathSanitizerTests
{
    [Theory]
    [InlineData("AC/DC", "AC_DC")]
    [InlineData("What? Why*", "What_ Why_")]
    [InlineData("a<b>c:d\"e|f", "a_b_c_d_e_f")]
    [InlineData("two   spaces", "two spaces")]
    public void InvalidCharacters_BecomeUnderscores(string input, string expected) =>
        Assert.Equal(expected, PathSanitizer.CleanValue(input));

    [Theory]
    [InlineData("Song.mp3.", "Song.mp3")]
    [InlineData("Song   ", "Song")]
    [InlineData("CON", "_CON")]
    [InlineData("con.mp3", "_con.mp3")]
    [InlineData("NUL.txt", "_NUL.txt")]
    [InlineData("COM1", "_COM1")]
    [InlineData("LPT9.flac", "_LPT9.flac")]
    [InlineData("Console.mp3", "Console.mp3")]
    [InlineData("", "_")]
    [InlineData("...", "_")]
    public void Names_AreMadeLegal(string input, string expected) =>
        Assert.Equal(expected, PathSanitizer.Segment(input));

    [Fact]
    public void Greek_IsKeptAsIs() =>
        Assert.Equal("Χάρις Αλεξίου - Κάτι άλλο.mp3", PathSanitizer.Segment("Χάρις Αλεξίου - Κάτι άλλο.mp3"));

    [Fact]
    public void ALongName_IsCutTo255_KeepsItsExtension_AndDoesNotSplitASurrogatePair()
    {
        var name = new string('a', 253) + "😀😀.mp3";
        var result = PathSanitizer.Segment(name);

        Assert.True(result.Length <= 255);
        Assert.EndsWith(".mp3", result);
        Assert.DoesNotContain('\uFFFD', result);
        Assert.False(char.IsHighSurrogate(result[^5])); // the char before ".mp3" is not a dangling high surrogate
    }
}

public class FileNameTemplateTests
{
    private static RenameSource Src(string path = @"C:\Music\old.mp3", string? artist = "Madonna", string? title = "Frozen", int? track = 3, double? bpm = 112.4) =>
        new(1, path, artist, title, "Ray of Light", "Madonna", 1998, track, "Pop", bpm, "8A");

    [Fact]
    public void Tokens_AreFilled()
    {
        var t = FileNameTemplate.Parse("{Artist} - {Title}");
        Assert.Null(t.Error);
        Assert.Equal("Madonna - Frozen", t.Render(Src(), false).Text);
    }

    [Fact]
    public void Track_PadsWithZeros_AndBpmKeepsOneDecimalOnlyWhenNotWhole()
    {
        var t = FileNameTemplate.Parse("{Track:00} {Bpm} {Year} {Key} {Album} {AlbumArtist} {Genre}");
        Assert.Equal("03 112.4 1998 8A Ray of Light Madonna Pop", t.Render(Src(), false).Text);
        Assert.StartsWith("03 120 ", t.Render(Src(bpm: 120.02), false).Text);
    }

    [Fact]
    public void ExtensionAndFileName_ComeFromTheFile()
    {
        var t = FileNameTemplate.Parse("{FileName}.{Extension}");
        Assert.True(t.HasExtensionToken);
        Assert.Equal("old.mp3", t.Render(Src(), false).Text);
    }

    [Fact]
    public void CaseModifiers_ApplyOnlyWhenAsked()
    {
        Assert.Equal("MADONNA frozen", FileNameTemplate.Parse("{Artist:upper} {Title:lower}").Render(Src(), false).Text);
        Assert.Equal("Madonna", FileNameTemplate.Parse("{Artist:title}").Render(Src(artist: "mADONNA"), false).Text);
        Assert.Equal("madonna", FileNameTemplate.Parse("{Artist}").Render(Src(artist: "madonna"), false).Text);
    }

    [Fact]
    public void GreekCase_IsHandledByTheInvariantCulture() =>
        Assert.Equal("ΑΛΕΞΊΟΥ", FileNameTemplate.Parse("{Artist:upper}").Render(Src(artist: "Αλεξίου"), false).Text);

    [Fact]
    public void ASlashInATagValue_CannotCreateAFolder() =>
        Assert.Equal("AC_DC - Frozen", FileNameTemplate.Parse("{Artist} - {Title}").Render(Src(artist: "AC/DC"), false).Text);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{Artist")]
    [InlineData("Artist}")]
    [InlineData("{Nope}")]
    [InlineData("{Track:xx}")]
    [InlineData("{Artist:shout}")]
    [InlineData("{{Artist}}")]
    public void BadTemplates_ReportAnError(string text) => Assert.NotNull(FileNameTemplate.Parse(text).Error);

    [Fact]
    public void MissingTags_AreReported_OrFilledWithPlaceholdersWhenAsked()
    {
        var t = FileNameTemplate.Parse("{Artist} - {Title}");
        var missing = t.Render(Src(artist: "  ", title: null), false);
        Assert.Null(missing.Text);
        Assert.Equal(["Artist", "Title"], missing.Missing);

        Assert.Equal("Unknown Artist - Unknown Title", t.Render(Src(artist: null, title: null), true).Text);
    }

    [Fact]
    public void ATrackNumberOfZero_CountsAsMissing() =>
        Assert.Null(FileNameTemplate.Parse("{Track:00}").Render(Src(track: 0), false).Text);
}

public class FileOperationPlannerTests
{
    private sealed class Probe(params string[] existing) : IFileProbe
    {
        public HashSet<string> Files { get; } = new(existing, StringComparer.OrdinalIgnoreCase);
        public bool Exists(string path) => Files.Contains(path);
    }

    private static RenameSource Src(long id, string path, string? artist = "A", string? title = "T") =>
        new(id, path, artist, title, null, null, null, null, null, null, null);

    private static readonly RenameOptions ByName = new("{Artist} - {Title}");

    [Fact]
    public void ASimpleRename_KeepsTheFolder_AndTheExtension()
    {
        var probe = new Probe(@"C:\M\x.mp3");
        var plan = FileOperationPlanner.PlanRename([Src(1, @"C:\M\x.mp3")], ByName, probe);

        var item = Assert.Single(plan);
        Assert.Equal(PlanStatus.Rename, item.Status);
        Assert.Equal(@"C:\M\A - T.mp3", item.NewPath);
        Assert.True(item.WillMove);
    }

    [Fact]
    public void ANameAlreadyRight_IsUnchanged()
    {
        var probe = new Probe(@"C:\M\A - T.mp3");
        var item = Assert.Single(FileOperationPlanner.PlanRename([Src(1, @"C:\M\A - T.mp3")], ByName, probe));
        Assert.Equal(PlanStatus.Unchanged, item.Status);
        Assert.False(item.WillMove);
    }

    [Fact]
    public void OnlyTheCaseChanging_IsCaseOnly_NotACollisionWithItself()
    {
        var probe = new Probe(@"C:\M\a - t.mp3");
        var item = Assert.Single(FileOperationPlanner.PlanRename([Src(1, @"C:\M\a - t.mp3")], ByName, probe));
        Assert.Equal(PlanStatus.CaseOnly, item.Status);
        Assert.True(item.WillMove);
    }

    [Fact]
    public void ANameTakenOnDisk_IsSkippedWithAReason()
    {
        var probe = new Probe(@"C:\M\x.mp3", @"C:\M\a - t.MP3");
        var item = Assert.Single(FileOperationPlanner.PlanRename([Src(1, @"C:\M\x.mp3")], ByName, probe));
        Assert.Equal(PlanStatus.Skipped, item.Status);
        Assert.Contains("already exists", item.Reason);
    }

    [Fact]
    public void TwoFilesWithTheSameNewName_OnlyTheFirstGetsIt()
    {
        var probe = new Probe(@"C:\M\x.mp3", @"C:\M\y.mp3");
        var plan = FileOperationPlanner.PlanRename([Src(1, @"C:\M\x.mp3"), Src(2, @"C:\M\y.mp3")], ByName, probe);

        Assert.Equal(PlanStatus.Rename, plan[0].Status);
        Assert.Equal(PlanStatus.Skipped, plan[1].Status);
        Assert.Contains("same new name", plan[1].Reason);
    }

    [Fact]
    public void ASameNameInADifferentFolder_IsNotACollision()
    {
        var probe = new Probe(@"C:\M\x.mp3", @"C:\N\y.mp3");
        var plan = FileOperationPlanner.PlanRename([Src(1, @"C:\M\x.mp3"), Src(2, @"C:\N\y.mp3")], ByName, probe);
        Assert.All(plan, p => Assert.Equal(PlanStatus.Rename, p.Status));
    }

    [Fact]
    public void ARenameCannotLandOnAFileThatKeepsItsName()
    {
        var probe = new Probe(@"C:\M\x.mp3", @"C:\M\A - T.mp3");
        var plan = FileOperationPlanner.PlanRename([Src(1, @"C:\M\x.mp3"), Src(2, @"C:\M\A - T.mp3")], ByName, probe);

        Assert.Equal(PlanStatus.Skipped, plan[0].Status);
        Assert.Equal(PlanStatus.Unchanged, plan[1].Status);
    }

    [Fact]
    public void AMissingTag_SkipsTheTrack_UnlessPlaceholdersAreOn()
    {
        var probe = new Probe(@"C:\M\x.mp3");
        var skipped = Assert.Single(FileOperationPlanner.PlanRename([Src(1, @"C:\M\x.mp3", artist: null)], ByName, probe));
        Assert.Equal(PlanStatus.Skipped, skipped.Status);
        Assert.Contains("{Artist}", skipped.Reason);

        var filled = Assert.Single(FileOperationPlanner.PlanRename([Src(1, @"C:\M\x.mp3", artist: null)], ByName with { UsePlaceholders = true }, probe));
        Assert.Equal(@"C:\M\Unknown Artist - T.mp3", filled.NewPath);
    }

    [Fact]
    public void AFileThatIsGone_IsSkipped_NotRenamed()
    {
        var item = Assert.Single(FileOperationPlanner.PlanRename([Src(1, @"E:\Offline\x.mp3")], ByName, new Probe()));
        Assert.Equal(PlanStatus.Skipped, item.Status);
        Assert.Contains("not found", item.Reason);
    }

    [Fact]
    public void ATooLongPath_IsSkipped()
    {
        var dir = @"C:\" + new string('d', 240);
        var probe = new Probe(dir + @"\x.mp3");
        var item = Assert.Single(FileOperationPlanner.PlanRename([Src(1, dir + @"\x.mp3", title: new string('t', 100))], ByName, probe));
        Assert.Equal(PlanStatus.Skipped, item.Status);
        Assert.Contains("characters long", item.Reason);
    }

    [Fact]
    public void BadTemplate_SkipsEverything_WithTheTemplateError()
    {
        var probe = new Probe(@"C:\M\x.mp3");
        var item = Assert.Single(FileOperationPlanner.PlanRename([Src(1, @"C:\M\x.mp3")], new RenameOptions("{Nope}"), probe));
        Assert.Equal(PlanStatus.Skipped, item.Status);
        Assert.Contains("Unknown token", item.Reason);
    }

    [Fact]
    public void Sidecars_TravelWithTheTrack_ButCoverImagesNeverDo()
    {
        var probe = new Probe(@"C:\M\x.mp3", @"C:\M\x.lrc", @"C:\M\x.cue", @"C:\M\cover.jpg", @"C:\M\folder.jpg");
        var item = Assert.Single(FileOperationPlanner.PlanRename([Src(1, @"C:\M\x.mp3")], ByName, probe));

        Assert.Equal([@"C:\M\A - T.lrc", @"C:\M\A - T.cue"], item.Sidecars.Select(s => s.NewPath));
        Assert.DoesNotContain(item.Sidecars, s => s.OldPath.Contains("cover") || s.OldPath.Contains("folder"));
    }

    [Fact]
    public void ASidecarCollision_SkipsTheWholeTrack()
    {
        var probe = new Probe(@"C:\M\x.mp3", @"C:\M\x.lrc", @"C:\M\A - T.lrc");
        var item = Assert.Single(FileOperationPlanner.PlanRename([Src(1, @"C:\M\x.mp3")], ByName, probe));
        Assert.Equal(PlanStatus.Skipped, item.Status);
        Assert.Contains(".lrc", item.Reason);
    }

    [Fact]
    public void Sidecars_CanBeTurnedOff()
    {
        var probe = new Probe(@"C:\M\x.mp3", @"C:\M\x.lrc");
        var item = Assert.Single(FileOperationPlanner.PlanRename([Src(1, @"C:\M\x.mp3")], ByName with { IncludeSidecars = false }, probe));
        Assert.Empty(item.Sidecars);
    }

    [Fact]
    public void ReservedAndIllegalNames_AreSanitized()
    {
        var probe = new Probe(@"C:\M\x.mp3");
        var item = Assert.Single(FileOperationPlanner.PlanRename([Src(1, @"C:\M\x.mp3", artist: "AC/DC", title: "Why?")], ByName, probe));
        Assert.Equal(@"C:\M\AC_DC - Why_.mp3", item.NewPath);

        var con = Assert.Single(FileOperationPlanner.PlanRename([Src(1, @"C:\M\x.mp3", artist: "CON", title: "x")], new RenameOptions("{Artist}"), probe));
        Assert.Equal(@"C:\M\_CON.mp3", con.NewPath);
    }
}
