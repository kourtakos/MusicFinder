using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using MusicFinder.Application.Duplicates;
using MusicFinder.Infrastructure.Indexing;
using MusicFinder.Infrastructure.Metadata;
using MusicFinder.Infrastructure.Persistence.Repositories;

namespace MusicFinder.IntegrationTests;

/// <summary>Probable duplicates from a real scan of generated files: read-only, and "not a duplicate" survives.</summary>
public class ProbableDuplicateStoreTests
{
    private static string Mp3(string dir, string name, string artist, string title, int frames)
    {
        var path = Path.Combine(dir, name);
        using (var fs = File.Create(path))
        {
            var frame = new byte[417];
            frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90;
            for (var i = 0; i < frames; i++) fs.Write(frame);
        }
        using var tl = TagLib.File.Create(path);
        tl.Tag.Performers = [artist];
        tl.Tag.Title = title;
        tl.Save();
        return path;
    }

    private static string Snapshot(string dir) => string.Join("|", Directory.EnumerateFiles(dir).OrderBy(f => f)
        .Select(f => f + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))));

    [Fact]
    public async Task AScan_FindsTheSameRecordingAndTheRemixSeparately_ChangesNothing_AndDismissalsStick()
    {
        await using var db = new TestDb();
        var music = Path.Combine(db.Dir, "Μουσική");
        Directory.CreateDirectory(music);
        Mp3(music, "a.mp3", "Madonna", "Frozen", 40);
        Mp3(music, "b copy.mp3", "Madonna", "Frozen", 41);
        Mp3(music, "remix.mp3", "Madonna", "Frozen (Thin White Duke Remix)", 40);
        Mp3(music, "other.mp3", "Madonna", "Vogue", 40);
        Mp3(music, "greek.mp3", "Χάρις Αλεξίου", "Σ' αγαπώ", 40);
        Mp3(music, "greeklish.mp3", "Xaris Alexiou", "S agapo", 40);
        var root = await db.Roots.AddAsync(music);
        await new LibraryScanner(db.Roots, db.Folders, db.Tracks, new TagLibMetadataReader(), NullLogger<LibraryScanner>.Instance).ScanRootAsync(root.Id, null, default);
        var before = Snapshot(music);
        var store = new SongCandidateRepository(db.Factory, db.Writes);
        var service = new ProbableDuplicateService(store);

        var result = await service.FindAsync(default);

        Assert.Equal(2, result.SameRecording.Count);                                          // Frozen twice, Greek/Greeklish pair
        var frozen = result.SameRecording.Single(g => g.Heading.Contains("Frozen"));
        Assert.Equal(["a.mp3", "b copy.mp3"], frozen.Members.Select(m => Path.GetFileName(m.FullPath)).Order());
        var versions = Assert.Single(result.OtherVersions);
        Assert.Equal(3, versions.Members.Count);                                              // the two Frozen files and the remix
        Assert.DoesNotContain(result.SameRecording.SelectMany(g => g.Members), m => m.FullPath.EndsWith("remix.mp3"));
        Assert.Equal(before, Snapshot(music));                                                // not one byte changed

        await service.DismissAsync(frozen.Key, default);

        var again = await new ProbableDuplicateService(new SongCandidateRepository(db.Factory, db.Writes)).FindAsync(default);
        Assert.Single(again.SameRecording);
        Assert.Single(again.OtherVersions);                                                    // a separate decision
    }

    [Fact]
    public async Task AnEmptyLibrary_GivesNothing()
    {
        await using var db = new TestDb();
        var result = await new ProbableDuplicateService(new SongCandidateRepository(db.Factory, db.Writes)).FindAsync(default);
        Assert.Empty(result.SameRecording);
        Assert.Empty(result.OtherVersions);
    }
}
