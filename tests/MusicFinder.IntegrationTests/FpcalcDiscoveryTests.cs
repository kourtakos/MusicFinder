using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure.Identification;

namespace MusicFinder.IntegrationTests;

/// <summary>Where the app looks for fpcalc.exe, and what it offers from the Downloads folder.</summary>
public sealed class FpcalcDiscoveryTests : IDisposable
{
    private sealed class FakeSettings : ISettingsStore
    {
        public AppSettings Value { get; } = new();
        public AppSettings Load() => Value;
        public void Save(AppSettings settings) { }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mf-fpcalc-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSettings _settings = new();

    public FpcalcDiscoveryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { /* temp folder */ }
    }

    private string Touch(params string[] parts)
    {
        var path = Path.Combine([_dir, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    // ===== the locator =====

    [Fact]
    public void TheToolsFolder_IsFoundBeforeBesideTheProgramAndPath()
    {
        var tools = Touch("data", "tools", "fpcalc.exe");
        Touch("app", "fpcalc.exe");
        Touch("onpath", "fpcalc.exe");

        var found = new FpcalcLocator(_settings, Path.Combine(_dir, "app"), Path.Combine(_dir, "onpath"), Path.Combine(_dir, "data", "tools")).Locate();

        Assert.Equal(tools, found!.Path);
        Assert.Equal("MusicFinder's tools folder", found.Source);
    }

    [Fact]
    public void AnExplicitPathInTheSettings_StillWinsOverTheToolsFolder()
    {
        Touch("data", "tools", "fpcalc.exe");
        var mine = Touch("mine", "fpcalc.exe");
        _settings.Value.FpcalcPath = mine;

        var found = new FpcalcLocator(_settings, Path.Combine(_dir, "app"), "", Path.Combine(_dir, "data", "tools")).Locate();

        Assert.Equal(mine, found!.Path);
        Assert.Equal("settings", found.Source);
    }

    [Fact]
    public void WithoutAToolsFolderOrWithAnEmptyOne_TheOldOrderStillWorks()
    {
        var beside = Touch("app", "fpcalc.exe");
        Directory.CreateDirectory(Path.Combine(_dir, "data", "tools"));

        Assert.Equal(beside, new FpcalcLocator(_settings, Path.Combine(_dir, "app"), "").Locate()!.Path);
        Assert.Equal(beside, new FpcalcLocator(_settings, Path.Combine(_dir, "app"), "", Path.Combine(_dir, "data", "tools")).Locate()!.Path);
        Assert.Null(new FpcalcLocator(_settings, Path.Combine(_dir, "nowhere"), "", Path.Combine(_dir, "data", "tools")).Locate());
    }

    [Fact]
    public void TheToolsFolder_IsExposedForTheSetup()
    {
        var tools = Path.Combine(_dir, "data", "tools");

        Assert.Equal(tools, new FpcalcLocator(_settings, toolsDirectory: tools).ToolsDirectory);
        Assert.Null(new FpcalcLocator(_settings).ToolsDirectory);
    }

    // ===== the Downloads folder =====

    [Fact]
    public void Downloads_OffersTheZip_TheLooseExe_AndTheUnzippedFolder_NewestFirst()
    {
        var zip = Touch("dl", "chromaprint-fpcalc-1.6.2-windows-x86_64.zip");
        var loose = Touch("dl", "fpcalc.exe");
        var unzipped = Touch("dl", "chromaprint-fpcalc-1.6.2-windows-x86_64", "fpcalc.exe");
        Touch("dl", "notes.txt");
        Touch("dl", "other-tool.exe");
        File.SetLastWriteTimeUtc(zip, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(loose, new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(unzipped, new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc));

        var found = FpcalcDownloads.Find(Path.Combine(_dir, "dl"));

        Assert.Equal([loose, unzipped, zip], found.Select(c => c.Path));
        Assert.Equal([FpcalcCandidateKind.Exe, FpcalcCandidateKind.Exe, FpcalcCandidateKind.Zip], found.Select(c => c.Kind));
    }

    [Fact]
    public void Downloads_DoesNotSearchDeeper_AndIgnoresOtherZips()
    {
        Touch("dl", "stuff", "nested", "fpcalc.exe");
        Touch("dl", "some-other-1.0.zip");
        Touch("dl", "chromaprint-fpcalc-1.6.2-windows-x86_64", "bin", "fpcalc.exe");      // two levels down: not offered

        Assert.Empty(FpcalcDownloads.Find(Path.Combine(_dir, "dl")));
    }

    [Fact]
    public void Downloads_MissingOrNullFolder_OffersNothing()
    {
        Assert.Empty(FpcalcDownloads.Find(null));
        Assert.Empty(FpcalcDownloads.Find(""));
        Assert.Empty(FpcalcDownloads.Find(Path.Combine(_dir, "no-such-folder")));
    }
}
