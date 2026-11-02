using MusicFinder.Application.Backup;
using MusicFinder.Domain.Models;

namespace MusicFinder.UnitTests;

public class PathRemapperTests
{
    private static PathRemapper R(params (string Old, string New)[] m) => new(m.Select(x => new PathMapping(x.Old, x.New)));

    [Theory]
    [InlineData(@"D:\DJ Music\House\a.mp3", @"E:\DJ Music\House\a.mp3")]
    [InlineData(@"d:\dj music\House\a.mp3", @"E:\DJ Music\House\a.mp3")]
    [InlineData(@"D:/DJ Music/House/a.mp3", @"E:\DJ Music\House\a.mp3")]
    [InlineData(@"D:\DJ Music", @"E:\DJ Music")]
    public void ThePrefixIsReplaced_IgnoringCaseAndSlashStyle_AndTheRestKeepsItsCasing(string path, string expected) =>
        Assert.Equal(expected, R((@"D:\DJ Music", @"E:\DJ Music")).Apply(path));

    [Fact]
    public void AFolderWithTheSamePrefixButAnotherName_IsNotMatched()
    {
        var r = R((@"D:\DJ Music", @"E:\DJ Music"));
        Assert.Equal(@"D:\DJ Music2\a.mp3", r.Apply(@"D:\DJ Music2\a.mp3"));
        Assert.Equal(@"D:\DJ Musical\a.mp3", r.Apply(@"D:\DJ Musical\a.mp3"));
    }

    [Fact]
    public void TrailingSeparatorsOnTheMapping_DoNotMatter() =>
        Assert.Equal(@"E:\Music\a.mp3", R((@"D:\Music\", @"E:\Music\")).Apply(@"D:\Music\a.mp3"));

    [Fact]
    public void DriveRoots_AreMapped()
    {
        var r = R((@"D:\", @"E:\"));
        Assert.Equal(@"E:\x\a.mp3", r.Apply(@"D:\x\a.mp3"));
        Assert.Equal(@"E:\", r.Apply(@"D:\"));
    }

    [Theory]
    [InlineData(@"D:\DJ Music\a.mp3", @"\\nas\music\DJ Music\a.mp3")]
    public void AFolderCanMoveToAShare(string path, string expected) =>
        Assert.Equal(expected, R((@"D:", @"\\nas\music")).Apply(path));

    [Fact]
    public void AShareCanMoveToAnotherShare_AndExtendedLengthPrefixesAreUnderstood()
    {
        Assert.Equal(@"\\new\share\a.mp3", R((@"\\old\share", @"\\new\share")).Apply(@"\\old\share\a.mp3"));
        Assert.Equal(@"\\new\share\a.mp3", R((@"\\?\UNC\old\share", @"\\new\share")).Apply(@"\\old\share\a.mp3"));
    }

    [Fact]
    public void TheLongestPrefixWins_AndAPathIsMappedOnlyOnce()
    {
        var r = R((@"D:\Music", @"E:\Music"), (@"D:\Music\Greek", @"F:\Greek"), (@"E:\Music", @"G:\Music"));

        Assert.Equal(@"F:\Greek\a.mp3", r.Apply(@"D:\Music\Greek\a.mp3"));
        Assert.Equal(@"E:\Music\a.mp3", r.Apply(@"D:\Music\a.mp3"));            // not chained on to G:
    }

    [Fact]
    public void TwoDrivesCanBeSwapped()
    {
        var r = R((@"D:\", @"E:\"), (@"E:\", @"D:\"));
        Assert.Equal(@"E:\a.mp3", r.Apply(@"D:\a.mp3"));
        Assert.Equal(@"D:\b.mp3", r.Apply(@"E:\b.mp3"));
    }

    [Fact]
    public void GreekAndNonAsciiNames_AreKeptAndMatchedIgnoringCase() =>
        Assert.Equal(@"E:\Μουσική\Ελληνικά\α.mp3", R((@"d:\ΜΟΥΣΙΚΉ", @"E:\Μουσική")).Apply(@"D:\Μουσική\Ελληνικά\α.mp3"));

    [Fact]
    public void LongPaths_AreHandled()
    {
        var tail = string.Join("\\", Enumerable.Repeat("verylongfoldername", 20)) + "\a.mp3";
        Assert.Equal(@"E:\M\" + tail, R((@"D:\M", @"E:\M")).Apply(@"D:\M\" + tail));
    }

    [Fact]
    public void WithNoMapping_NothingChanges()
    {
        Assert.True(R().IsEmpty);
        Assert.Equal(@"D:\a.mp3", R().Apply(@"D:\a.mp3"));
    }

    [Fact]
    public void ThePrefixItselfIsReported_ForRenamingTheFolderRow()
    {
        R((@"D:\DJ Music", @"E:\Tunes")).Apply(@"D:\DJ Music", out var itself);
        Assert.True(itself);
        R((@"D:\DJ Music", @"E:\Tunes")).Apply(@"D:\DJ Music\x", out var notItself);
        Assert.False(notItself);
    }

    [Fact]
    public void MergedFolders_AreReportedAsCollisions() =>
        Assert.Equal([@"E:\Music\a.mp3"], PathRemapper.FindCollisions([@"E:\Music\a.mp3", @"e:\music\A.MP3", @"E:\Music\b.mp3"]).Select(s => s));
}
