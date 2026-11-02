using System.Xml.Linq;

namespace MusicFinder.UnitTests;

/// <summary>The release ships THIRD-PARTY-NOTICES.md; it must name every package we reference that is not test-only.</summary>
public class ThirdPartyNoticesTests
{
    // Used to build and test only; never part of the published folder.
    private static readonly string[] TestOnly =
    [
        "Microsoft.NET.Test.Sdk", "xunit", "xunit.runner.visualstudio", "coverlet.collector", "Microsoft.Extensions.TimeProvider.Testing",
    ];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Packages.props"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Directory.Packages.props not found above the test folder.");
    }

    [Fact]
    public void EveryShippedPackage_IsNamedInTheNotices()
    {
        var root = RepoRoot();
        var notices = File.ReadAllText(Path.Combine(root, "THIRD-PARTY-NOTICES.md"));
        var packages = XDocument.Load(Path.Combine(root, "Directory.Packages.props"))
            .Descendants("PackageVersion")
            .Select(p => (Id: (string)p.Attribute("Include")!, Version: (string)p.Attribute("Version")!))
            .Where(p => !TestOnly.Contains(p.Id, StringComparer.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(packages);
        var missing = packages.Where(p => !notices.Contains(p.Id, StringComparison.OrdinalIgnoreCase)).Select(p => p.Id).ToList();
        Assert.True(missing.Count == 0, "Add to THIRD-PARTY-NOTICES.md: " + string.Join(", ", missing));
    }

    [Fact]
    public void TheNotices_StateTheLgplTerms_AndThatFpcalcIsNotBundled()
    {
        var notices = File.ReadAllText(Path.Combine(RepoRoot(), "THIRD-PARTY-NOTICES.md"));

        Assert.Contains("LGPL-2.1", notices);
        Assert.Contains("TagLibSharp.dll", notices);
        Assert.Contains("not** included", notices);
    }

    [Fact]
    public void ThePublishedVersion_IsSetOnce_InDirectoryBuildProps()
    {
        var props = XDocument.Load(Path.Combine(RepoRoot(), "Directory.Build.props"));
        var version = props.Descendants("Version").Select(v => v.Value).SingleOrDefault();

        Assert.NotNull(version);
        Assert.True(System.Version.TryParse(version, out _), $"'{version}' is not a numeric version.");
    }
}
