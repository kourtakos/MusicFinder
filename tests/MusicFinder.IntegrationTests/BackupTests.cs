using System.IO.Compression;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure.Backup;
using MusicFinder.Infrastructure.Settings;

namespace MusicFinder.IntegrationTests;

/// <summary>Backup of a live WAL database: consistent, verified, secret-free by default, and any damaged archive is refused. Temp folders only.</summary>
public class BackupTests
{
    private sealed class Env : IAsyncDisposable
    {
        public TestDb Db { get; } = new();
        public JsonSettingsStore Settings { get; }
        public string ArtworkDir { get; }
        public LibraryBackupService Service { get; }
        public long FolderId { get; private set; }

        public Env()
        {
            Settings = new JsonSettingsStore(Path.Combine(Db.Dir, "settings.json"));
            Settings.Save(new AppSettings { AcoustIdApiKey = "SECRET-KEY", MusicBrainzContact = "me@example.org", IdentificationEnabled = true, IdentificationConsentUtc = DateTime.UtcNow });
            ArtworkDir = Path.Combine(Db.Dir, "Cache", "Artwork");
            Directory.CreateDirectory(ArtworkDir);
            File.WriteAllBytes(Path.Combine(ArtworkDir, "cover1.jpg"), [1, 2, 3, 4]);
            Service = new LibraryBackupService(Db.Factory, Settings, ArtworkDir, "1.2.3");
        }

        public async Task SeedAsync(int tracks)
        {
            var root = await Db.Roots.AddAsync(@"D:\DJ Music");
            FolderId = await Db.Folders.UpsertAsync(root.Id, null, @"D:\DJ Music", "DJ Music");
            await Db.Tracks.UpsertBatchAsync(Enumerable.Range(0, tracks).Select(i => TestDb.MakeTrack(FolderId, $@"D:\DJ Music\t{i}.mp3", $"Song {i}", "Artist")).ToList());
        }

        public string Target(string name = "b") => Path.Combine(Db.Dir, "out", name + LibraryBackupService.FileExtension);

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private static string Read(string zipPath, string entry)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        using var r = new StreamReader(zip.GetEntry(entry)!.Open());
        return r.ReadToEnd();
    }

    [Fact]
    public async Task ABackup_HasAManifestWithMatchingHashesCountsAndRoots_AndAnIntactDatabase()
    {
        await using var e = new Env();
        await e.SeedAsync(50);

        var result = await e.Service.BackupAsync(new BackupRequest(e.Target()), default);

        Assert.EndsWith(".musicfinder-backup.zip", result.Path);
        var manifest = await e.Service.InspectAsync(result.Path, default);                  // re-verifies every payload
        Assert.Equal(1, manifest.FormatVersion);
        Assert.Equal(MusicFinder.Infrastructure.Persistence.MigrationRunner.LatestVersion, manifest.SchemaVersion);
        Assert.Equal("1.2.3", manifest.AppVersion);
        Assert.Equal(50, manifest.Counts["Tracks"]);
        var root = Assert.Single(manifest.Roots);
        Assert.Equal((@"D:\DJ Music", 50), (root.Path, root.TrackCount));
        Assert.Contains(manifest.Payloads, p => p.Name == "library.db" && p.Size > 0);

        var extracted = Path.Combine(e.Db.Dir, "extracted.db");
        using (var zip = ZipFile.OpenRead(result.Path)) zip.GetEntry("library.db")!.ExtractToFile(extracted);
        using var conn = new SqliteConnection($"Data Source={extracted};Pooling=False");
        conn.Open();
        Assert.Equal("ok", conn.ExecuteScalar<string>("PRAGMA integrity_check"));
        Assert.Equal(50, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM Tracks"));
        Assert.Equal("delete", conn.ExecuteScalar<string>("PRAGMA journal_mode"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(result.Path)!, "*.tmp"));        // nothing left behind
    }

    [Fact]
    public async Task ABackupTakenWhileTheLibraryIsBeingWritten_IsConsistent()
    {
        await using var e = new Env();
        await e.SeedAsync(200);
        using var stop = new CancellationTokenSource();
        var writer = Task.Run(async () =>
        {
            var i = 1000;
            while (!stop.IsCancellationRequested)
                await e.Db.Tracks.UpsertBatchAsync([TestDb.MakeTrack(e.FolderId, $@"D:\DJ Music\live{i++}.mp3", "Live", "W")]);
        });

        var results = new List<BackupResult>();
        for (var i = 0; i < 3; i++) results.Add(await e.Service.BackupAsync(new BackupRequest(e.Target("w" + i)), default));
        stop.Cancel();
        await writer;

        foreach (var r in results)
        {
            var manifest = await e.Service.InspectAsync(r.Path, default);
            Assert.True(manifest.Counts["Tracks"] >= 200);
            var extracted = Path.Combine(e.Db.Dir, Path.GetFileName(r.Path) + ".db");
            using (var zip = ZipFile.OpenRead(r.Path)) zip.GetEntry("library.db")!.ExtractToFile(extracted, true);
            using var conn = new SqliteConnection($"Data Source={extracted};Pooling=False");
            conn.Open();
            Assert.Equal("ok", conn.ExecuteScalar<string>("PRAGMA integrity_check"));
            Assert.Equal(manifest.Counts["Tracks"], conn.ExecuteScalar<int>("SELECT COUNT(*) FROM Tracks"));   // the manifest describes this very snapshot
        }
    }

    [Fact]
    public async Task Secrets_AreLeftOutByDefault_AndIncludedOnlyOnRequest()
    {
        await using var e = new Env();
        await e.SeedAsync(1);

        var plain = await e.Service.BackupAsync(new BackupRequest(e.Target("plain")), default);
        var withSecrets = await e.Service.BackupAsync(new BackupRequest(e.Target("secret"), IncludeSecrets: true), default);

        var plainJson = Read(plain.Path, "settings.json");
        Assert.DoesNotContain("SECRET-KEY", plainJson);
        Assert.DoesNotContain("me@example.org", plainJson);
        var restored = JsonSerializer.Deserialize<AppSettings>(plainJson)!;
        Assert.False(restored.IdentificationEnabled);
        Assert.False(plain.Manifest.IncludesSecrets);

        Assert.Contains("SECRET-KEY", Read(withSecrets.Path, "settings.json"));
        Assert.True(withSecrets.Manifest.IncludesSecrets);
        Assert.Equal("SECRET-KEY", e.Settings.Load().AcoustIdApiKey);                            // the live settings were not touched
    }

    [Fact]
    public async Task Artwork_IsIncludedOnlyOnRequest()
    {
        await using var e = new Env();
        await e.SeedAsync(1);

        var without = await e.Service.BackupAsync(new BackupRequest(e.Target("a")), default);
        var with = await e.Service.BackupAsync(new BackupRequest(e.Target("b"), IncludeArtwork: true), default);

        Assert.DoesNotContain(without.Manifest.Payloads, p => p.Kind == "artwork");
        Assert.Contains(with.Manifest.Payloads, p => p.Name == "artwork/cover1.jpg");
        await e.Service.InspectAsync(with.Path, default);
    }

    [Fact]
    public async Task ACancelledBackup_LeavesNoArchiveAndNoTemporaryFiles()
    {
        await using var e = new Env();
        await e.SeedAsync(10);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => e.Service.BackupAsync(new BackupRequest(e.Target()), cts.Token));

        var dir = Path.GetDirectoryName(e.Target())!;
        Assert.True(!Directory.Exists(dir) || Directory.GetFileSystemEntries(dir).Length == 0);
    }

    [Fact]
    public async Task DamagedArchives_AreRefusedWithAReason()
    {
        await using var e = new Env();
        await e.SeedAsync(20);
        var good = (await e.Service.BackupAsync(new BackupRequest(e.Target("good")), default)).Path;
        var bytes = File.ReadAllBytes(good);

        // truncated
        var truncated = Path.Combine(e.Db.Dir, "truncated.musicfinder-backup.zip");
        File.WriteAllBytes(truncated, bytes[..(bytes.Length / 2)]);
        await Assert.ThrowsAsync<BackupFormatException>(() => e.Service.InspectAsync(truncated, default));

        // one flipped byte inside the stored database
        var flipped = Path.Combine(e.Db.Dir, "flipped.musicfinder-backup.zip");
        var copy = (byte[])bytes.Clone();
        copy[200] ^= 0xFF;
        File.WriteAllBytes(flipped, copy);
        await Assert.ThrowsAsync<BackupFormatException>(() => e.Service.InspectAsync(flipped, default));

        // not a zip at all
        var junk = Path.Combine(e.Db.Dir, "junk.musicfinder-backup.zip");
        File.WriteAllText(junk, "this is not a backup");
        await Assert.ThrowsAsync<BackupFormatException>(() => e.Service.InspectAsync(junk, default));

        // a zip without manifest
        var noManifest = Path.Combine(e.Db.Dir, "nomanifest.musicfinder-backup.zip");
        using (var zip = ZipFile.Open(noManifest, ZipArchiveMode.Create)) zip.CreateEntry("library.db");
        var ex = await Assert.ThrowsAsync<BackupFormatException>(() => e.Service.InspectAsync(noManifest, default));
        Assert.Contains("manifest", ex.Message);

        // a format from the future
        var future = Path.Combine(e.Db.Dir, "future.musicfinder-backup.zip");
        File.Copy(good, future);
        using (var zip = ZipFile.Open(future, ZipArchiveMode.Update))
        {
            var manifest = JsonSerializer.Deserialize<BackupManifest>(Read(good, "manifest.json"))!;
            manifest.FormatVersion = 99;
            zip.GetEntry("manifest.json")!.Delete();
            using var w = zip.CreateEntry("manifest.json").Open();
            JsonSerializer.Serialize(w, manifest);
        }
        var future2 = await Assert.ThrowsAsync<BackupFormatException>(() => e.Service.InspectAsync(future, default));
        Assert.Contains("99", future2.Message);
    }

    [Fact]
    public void TheSecretRedactor_DoesNotChangeTheOriginal()
    {
        var original = new AppSettings { AcoustIdApiKey = "k", MusicBrainzContact = "c", LeftPanelWidth = 321 };
        var copy = MusicFinder.Application.Backup.SettingsRedactor.ForBackup(original, includeSecrets: false);

        Assert.Null(copy.AcoustIdApiKey);
        Assert.Equal(321, copy.LeftPanelWidth);
        Assert.Equal("k", original.AcoustIdApiKey);
    }
}
