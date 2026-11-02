using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using MusicFinder.Application.Backup;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure.Persistence;

namespace MusicFinder.Infrastructure.Backup;

/// <summary>
/// Backup and verification. The database is copied with SQLite's online backup (one consistent read while the app keeps writing),
/// never by copying the live file. Success is reported only after the finished archive has been opened again and every payload re-hashed.
/// </summary>
public sealed class LibraryBackupService : ILibraryBackupService
{
    public const string FileExtension = ".musicfinder-backup.zip";
    private const string ManifestName = "manifest.json";
    private const string DatabaseName = "library.db";
    private const string SettingsName = "settings.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly string[] CountedTables =
        ["Tracks", "Folders", "Genres", "GenreAliases", "CustomTags", "DjSessions", "PlayHistory", "TrackEvents", "IdentificationCache", "FileOperations"];

    private readonly SqliteConnectionFactory _factory;
    private readonly ISettingsStore _settings;
    private readonly string? _artworkDir;
    private readonly string _appVersion;

    public LibraryBackupService(SqliteConnectionFactory factory, ISettingsStore settings, string? artworkDir = null, string? appVersion = null)
    {
        _factory = factory;
        _settings = settings;
        _artworkDir = artworkDir;
        _appVersion = appVersion ?? typeof(LibraryBackupService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    public Task<BackupResult> BackupAsync(BackupRequest request, CancellationToken ct) => Task.Run(() => Backup(request, ct), ct);

    private BackupResult Backup(BackupRequest request, CancellationToken ct)
    {
        var target = request.TargetPath.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase) ? request.TargetPath : request.TargetPath + FileExtension;
        var dir = Path.GetDirectoryName(Path.GetFullPath(target))!;
        Directory.CreateDirectory(dir);
        var work = Path.Combine(dir, ".mf-backup-" + Guid.NewGuid().ToString("N"));
        var temp = target + ".tmp";
        Directory.CreateDirectory(work);
        try
        {
            var manifest = new BackupManifest
            {
                SchemaVersion = 0, AppVersion = _appVersion, CreatedUtc = DateTime.UtcNow, Portable = request.Portable,
                IncludesSecrets = request.IncludeSecrets, IncludesArtwork = request.IncludeArtwork
            };
            var snapshot = Path.Combine(work, DatabaseName);
            Snapshot(snapshot, manifest, ct);

            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                AddFile(zip, manifest, DatabaseName, "database", snapshot, ct);

                var settingsJson = JsonSerializer.Serialize(SettingsRedactor.ForBackup(_settings.Load(), request.IncludeSecrets), Json);
                AddBytes(zip, manifest, SettingsName, "settings", System.Text.Encoding.UTF8.GetBytes(settingsJson));

                if (request.IncludeArtwork && _artworkDir is not null && Directory.Exists(_artworkDir))
                    foreach (var file in Directory.EnumerateFiles(_artworkDir))
                    {
                        ct.ThrowIfCancellationRequested();
                        AddFile(zip, manifest, "artwork/" + Path.GetFileName(file), "artwork", file, ct);
                    }

                var entry = zip.CreateEntry(ManifestName, CompressionLevel.Optimal);
                using var es = entry.Open();
                JsonSerializer.Serialize(es, manifest, Json);
            }

            Verify(temp, ct);                                   // reopen and re-hash before calling it a backup
            File.Move(temp, target, overwrite: true);
            return new BackupResult(target, new FileInfo(target).Length, manifest);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { /* a leftover .tmp is harmless */ }
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>One consistent copy of the live database, checked for integrity, with the facts the manifest needs.</summary>
    private void Snapshot(string snapshotPath, BackupManifest manifest, CancellationToken ct)
    {
        using (var source = _factory.Open())
        using (var dest = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = snapshotPath, Pooling = false }.ToString()))
        {
            dest.Open();
            source.BackupDatabase(dest);
        }

        using var snap = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = snapshotPath, Pooling = false, Mode = SqliteOpenMode.ReadWrite }.ToString());
        snap.Open();
        snap.Execute("PRAGMA journal_mode=DELETE;");            // one self-contained file, no -wal beside it
        var integrity = snap.ExecuteScalar<string>("PRAGMA integrity_check;");
        if (integrity != "ok") throw new InvalidOperationException("The snapshot of the library failed its integrity check: " + integrity);

        manifest.SchemaVersion = snap.ExecuteScalar<int?>("SELECT MAX(Version) FROM SchemaVersions") ?? 0;
        foreach (var table in CountedTables)
        {
            ct.ThrowIfCancellationRequested();
            if (snap.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@table", new { table }) == 0) continue;
            manifest.Counts[table] = snap.ExecuteScalar<int>($"SELECT COUNT(*) FROM {table}");
        }
        manifest.Roots = snap.Query<(long Id, string Path, long Enabled, long Tracks)>(
            """
            SELECT r.Id, r.Path, r.Enabled, COUNT(t.Id)
            FROM LibraryRoots r LEFT JOIN Folders f ON f.RootId = r.Id LEFT JOIN Tracks t ON t.FolderId = f.Id
            GROUP BY r.Id ORDER BY r.Id
            """).Select(r => new BackupRoot(r.Id, r.Path, r.Enabled != 0, (int)r.Tracks)).ToList();
    }

    private static void AddFile(ZipArchive zip, BackupManifest manifest, string name, string kind, string path, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var input = File.OpenRead(path);
        using var output = entry.Open();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        long total = 0;
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.AppendData(buffer, 0, read);
            output.Write(buffer, 0, read);
            total += read;
        }
        manifest.Payloads.Add(new BackupPayload(name, kind, total, Convert.ToHexString(sha.GetHashAndReset())));
    }

    private static void AddBytes(ZipArchive zip, BackupManifest manifest, string name, string kind, byte[] bytes)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using (var output = entry.Open()) output.Write(bytes);
        manifest.Payloads.Add(new BackupPayload(name, kind, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))));
    }

    public Task<BackupManifest> InspectAsync(string archivePath, CancellationToken ct) => Task.Run(() => Verify(archivePath, ct), ct);

    /// <summary>Opens an archive, reads its manifest and re-hashes every payload. Anything unexpected is a <see cref="BackupFormatException"/> with the reason.</summary>
    internal static BackupManifest Verify(string archivePath, CancellationToken ct)
    {
        try
        {
            using var zip = ZipFile.OpenRead(archivePath);
            var manifestEntry = zip.GetEntry(ManifestName) ?? throw new BackupFormatException("This is not a MusicFinder backup: it has no manifest.");
            BackupManifest manifest;
            using (var s = manifestEntry.Open())
                manifest = JsonSerializer.Deserialize<BackupManifest>(s) ?? throw new BackupFormatException("The manifest is empty.");

            if (manifest.FormatVersion is < 1 or > BackupManifest.CurrentFormatVersion)
                throw new BackupFormatException($"This backup uses format {manifest.FormatVersion}, which this version of MusicFinder does not understand.");
            if (!manifest.Payloads.Any(p => p.Name == DatabaseName)) throw new BackupFormatException("The backup does not contain the library database.");

            foreach (var payload in manifest.Payloads)
            {
                ct.ThrowIfCancellationRequested();
                var entry = zip.GetEntry(payload.Name) ?? throw new BackupFormatException($"The backup is missing '{payload.Name}'.");
                using var s = entry.Open();
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 20];
                long total = 0;
                int read;
                while ((read = s.Read(buffer)) > 0) { sha.AppendData(buffer, 0, read); total += read; }
                if (total != payload.Size || !string.Equals(Convert.ToHexString(sha.GetHashAndReset()), payload.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new BackupFormatException($"'{payload.Name}' is damaged: its content does not match the checksum in the backup.");
            }
            return manifest;
        }
        catch (InvalidDataException ex) { throw new BackupFormatException("The file is not a readable backup archive: " + ex.Message); }
        catch (JsonException ex) { throw new BackupFormatException("The manifest cannot be read: " + ex.Message); }
    }
}
