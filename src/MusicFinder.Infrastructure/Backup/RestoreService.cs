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

/// <summary>What the next start has to do: written when a restore is staged, advanced phase by phase while it is applied.</summary>
public sealed class RestoreMarker
{
    public string Phase { get; set; } = RestorePhase.Staged;
    public string StagingDirectory { get; set; } = "";
    public string StagingDb { get; set; } = "";
    public string StagingSettings { get; set; } = "";
    public string StagingSha256 { get; set; } = "";
    public string SafetyBackup { get; set; } = "";
    public string? PreviousDb { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public static class RestorePhase
{
    public const string Staged = "Staged";
    public const string LiveMovedAside = "LiveMovedAside";
    public const string DbSwapped = "DbSwapped";
}

/// <summary>
/// Prepares a restore without touching the live library: a verified safety backup of what exists now, then the archive is extracted
/// to a staging folder, checked, upgraded to this version's schema and its paths remapped. <see cref="RestoreFinalizer"/> swaps it in at the next start.
/// </summary>
public sealed class RestoreService : ILibraryRestoreService
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly SqliteConnectionFactory _live;
    private readonly ILibraryBackupService _backup;
    private readonly ISettingsStore _settings;
    private readonly ILibraryWriteLock _lock;
    private readonly IFileOperationJournal _journal;
    private readonly AppPaths _paths;

    public RestoreService(SqliteConnectionFactory live, ILibraryBackupService backup, ISettingsStore settings,
        ILibraryWriteLock writeLock, IFileOperationJournal journal, AppPaths paths)
    {
        _live = live;
        _backup = backup;
        _settings = settings;
        _lock = writeLock;
        _journal = journal;
        _paths = paths;
    }

    public bool HasPendingRestore => File.Exists(_paths.RestoreMarkerPath);

    public void CancelPendingRestore()
    {
        if (!File.Exists(_paths.RestoreMarkerPath)) return;
        var marker = RestoreFinalizer.ReadMarker(_paths);
        // Only a restore that has not started to swap files can be forgotten.
        if (marker is { Phase: RestorePhase.Staged })
        {
            File.Delete(_paths.RestoreMarkerPath);
            if (Directory.Exists(marker.StagingDirectory)) Directory.Delete(marker.StagingDirectory, recursive: true);
        }
    }

    public async Task<RestorePreview> PreviewAsync(string archivePath, CancellationToken ct)
    {
        var manifest = await _backup.InspectAsync(archivePath, ct);
        var verdict = manifest.SchemaVersion > MigrationRunner.LatestVersion ? SchemaVerdict.TooNew
            : manifest.SchemaVersion < MigrationRunner.LatestVersion ? SchemaVerdict.WillBeUpgraded : SchemaVerdict.Current;

        var counts = new Dictionary<string, int>();
        using (var conn = _live.Open())
            foreach (var table in manifest.Counts.Keys)
                if (conn.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@table", new { table }) > 0)
                    counts[table] = conn.ExecuteScalar<int>($"SELECT COUNT(*) FROM {table}");

        var roots = manifest.Roots.Select(r => new RestoreRootPreview(r, Directory.Exists(r.Path))).ToList();
        return new RestorePreview(manifest, verdict, counts, roots);
    }

    public async Task<StagedRestore> StageAsync(RestoreRequest request, CancellationToken ct)
    {
        if (HasPendingRestore) throw new RestoreRefusedException("A restore is already waiting for the next start. Restart MusicFinder first, or cancel it.");

        var manifest = await _backup.InspectAsync(request.ArchivePath, ct);              // refuses damaged archives before anything else
        if (manifest.SchemaVersion > MigrationRunner.LatestVersion)
            throw new RestoreRefusedException($"This backup was made by a newer MusicFinder (database version {manifest.SchemaVersion}; this one understands up to {MigrationRunner.LatestVersion}). Update MusicFinder to restore it.");

        using var gate = _lock.TryEnter()
            ?? throw new RestoreRefusedException("A library scan or a file operation is running. Wait until it has finished.");
        if ((await _journal.GetUnfinishedAsync(ct)).Count > 0)
            throw new RestoreRefusedException("A rename or move was interrupted and has not been settled yet. Restart MusicFinder first so it can settle it.");

        Directory.CreateDirectory(_paths.DataDir);
        var needed = (File.Exists(_paths.DatabasePath) ? new FileInfo(_paths.DatabasePath).Length : 0) * 2 + new FileInfo(request.ArchivePath).Length * 2;
        try
        {
            var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_paths.DataDir))!).AvailableFreeSpace;
            if (free < needed) throw new RestoreRefusedException($"Not enough free space for the restore ({needed / (1024 * 1024):N0} MB needed).");
        }
        catch (ArgumentException) { /* a share without drive information: let the copy itself fail if it must */ }

        var mapper = new PathRemapper(request.Mappings);
        ThrowIfMappingsCollide(manifest, mapper);

        // 1. Safety backup of what exists now. If it cannot be written and verified, nothing else happens.
        Directory.CreateDirectory(_paths.BackupsDir);
        var safetyPath = Path.Combine(_paths.BackupsDir, $"safety-{DateTime.UtcNow:yyyyMMdd-HHmmss}" + LibraryBackupService.FileExtension);
        BackupResult safety;
        try { safety = await _backup.BackupAsync(new BackupRequest(safetyPath, IncludeSecrets: true), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new RestoreRefusedException("The safety backup of your current library could not be made, so nothing was changed: " + ex.Message);
        }

        // 2. Extract and check the archive in a staging folder.
        var staging = Path.Combine(_paths.RestoreDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var stagingDb = Path.Combine(staging, "staging.db");
            var stagingSettings = Path.Combine(staging, "settings.json");
            using (var zip = ZipFile.OpenRead(request.ArchivePath))
            {
                zip.GetEntry("library.db")!.ExtractToFile(stagingDb);
                var settingsEntry = zip.GetEntry("settings.json");
                if (settingsEntry is not null) settingsEntry.ExtractToFile(stagingSettings);
            }

            var (tracks, roots) = PrepareDatabase(stagingDb, manifest, mapper, ct);
            if (File.Exists(stagingSettings)) File.WriteAllText(stagingSettings, MergeSettings(File.ReadAllText(stagingSettings)));

            var marker = new RestoreMarker
            {
                Phase = RestorePhase.Staged, StagingDirectory = staging, StagingDb = stagingDb,
                StagingSettings = File.Exists(stagingSettings) ? stagingSettings : "",
                StagingSha256 = HashFile(stagingDb), SafetyBackup = safety.Path, CreatedUtc = DateTime.UtcNow
            };
            RestoreFinalizer.WriteMarker(_paths, marker);
            return new StagedRestore(staging, safety.Path, tracks, roots);
        }
        catch
        {
            try { Directory.Delete(staging, recursive: true); } catch (IOException) { }
            throw;
        }
    }

    private static void ThrowIfMappingsCollide(BackupManifest manifest, PathRemapper mapper)
    {
        if (mapper.IsEmpty) return;
        var collisions = PathRemapper.FindCollisions(manifest.Roots.Select(r => mapper.Apply(r.Path)));
        if (collisions.Count > 0)
            throw new RestoreRefusedException("Two library folders would end up in the same place (" + collisions[0] + "). Change the folder mapping.");
    }

    /// <summary>Integrity check, schema upgrade and path remap on the staging copy. Returns (tracks, roots) whose paths changed.</summary>
    private static (int Tracks, int Roots) PrepareDatabase(string stagingDb, BackupManifest manifest, PathRemapper mapper, CancellationToken ct)
    {
        var factory = new SqliteConnectionFactory(stagingDb);
        try
        {
            using (var conn = factory.Open())
            {
                var integrity = conn.ExecuteScalar<string>("PRAGMA integrity_check;");
                if (integrity != "ok") throw new RestoreRefusedException("The database inside the backup failed its integrity check: " + integrity);
            }
            if (manifest.SchemaVersion < MigrationRunner.LatestVersion) new MigrationRunner(factory).Migrate();

            var changed = (0, 0);
            if (!mapper.IsEmpty) changed = Remap(factory, mapper, ct);

            using (var conn = factory.Open())
            {
                conn.Execute("PRAGMA wal_checkpoint(TRUNCATE);");
                conn.Execute("PRAGMA journal_mode=DELETE;");
            }
            return changed;
        }
        finally { factory.ReleasePool(); }
    }

    /// <summary>
    /// Rewrites every stored path in one transaction. Track ids are never touched, so play history, genres, tags and analysis follow the files.
    /// The per-row search triggers are set aside and the search index is rebuilt once at the end, which is far cheaper than two index updates per track.
    /// </summary>
    private static (int Tracks, int Roots) Remap(SqliteConnectionFactory factory, PathRemapper mapper, CancellationToken ct)
    {
        using var conn = factory.Open();
        var triggers = conn.Query<(string Name, string Sql)>(
            "SELECT name, sql FROM sqlite_master WHERE type='trigger' AND tbl_name='Tracks' AND sql LIKE '%AFTER UPDATE%'").ToList();

        // Paths must stay unique after the mapping (case-insensitively), or the unique indexes would reject the update half-way.
        var trackPaths = conn.Query<(long Id, string Path)>("SELECT Id, FullPath FROM Tracks").ToList();
        var mapped = trackPaths.Select(t => mapper.Apply(t.Path)).ToList();
        var collisions = PathRemapper.FindCollisions(mapped);
        if (collisions.Count > 0) throw new RestoreRefusedException("Two tracks would end up with the same path (" + collisions[0] + "). Change the folder mapping.");

        using var tx = conn.BeginTransaction();
        foreach (var t in triggers) conn.Execute($"DROP TRIGGER \"{t.Name}\"", transaction: tx);

        var tracksChanged = 0;
        for (var i = 0; i < trackPaths.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (string.Equals(mapped[i], trackPaths[i].Path, StringComparison.Ordinal)) continue;
            conn.Execute("UPDATE Tracks SET FullPath = @p WHERE Id = @id", new { p = mapped[i], id = trackPaths[i].Id }, tx);
            tracksChanged++;
        }

        foreach (var f in conn.Query<(long Id, string Path)>("SELECT Id, FullPath FROM Folders", transaction: tx).ToList())
        {
            var to = mapper.Apply(f.Path, out var itself);
            if (string.Equals(to, f.Path, StringComparison.Ordinal)) continue;
            if (itself) conn.Execute("UPDATE Folders SET FullPath = @p, Name = @n WHERE Id = @id", new { p = to, n = LastSegment(to), id = f.Id }, tx);
            else conn.Execute("UPDATE Folders SET FullPath = @p WHERE Id = @id", new { p = to, id = f.Id }, tx);
        }

        var rootsChanged = 0;
        foreach (var r in conn.Query<(long Id, string Path)>("SELECT Id, Path FROM LibraryRoots", transaction: tx).ToList())
        {
            var to = mapper.Apply(r.Path);
            if (string.Equals(to, r.Path, StringComparison.Ordinal)) continue;
            conn.Execute("UPDATE LibraryRoots SET Path = @p WHERE Id = @id", new { p = to, id = r.Id }, tx);
            rootsChanged++;
        }

        if (conn.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='FileOperations'", transaction: tx) > 0)
            foreach (var o in conn.Query<(long Id, string Old, string New)>("SELECT Id, OldPath, NewPath FROM FileOperations", transaction: tx).ToList())
                conn.Execute("UPDATE FileOperations SET OldPath = @a, NewPath = @b WHERE Id = @id", new { a = mapper.Apply(o.Old), b = mapper.Apply(o.New), id = o.Id }, tx);

        conn.Execute("INSERT INTO TrackSearch(TrackSearch) VALUES ('rebuild')", transaction: tx);
        foreach (var t in triggers) conn.Execute(t.Sql, transaction: tx);
        tx.Commit();

        // The search index and the database must both be sound before this copy may replace the library.
        conn.Execute("INSERT INTO TrackSearch(TrackSearch) VALUES ('integrity-check')");
        var left = conn.Query<string>("SELECT FullPath FROM Tracks").Count(p => !string.Equals(mapper.Apply(p), p, StringComparison.Ordinal));
        if (left > 0) throw new RestoreRefusedException($"{left} paths were not remapped; the restore was stopped before anything was replaced.");
        return (tracksChanged, rootsChanged);
    }

    private static string LastSegment(string path)
    {
        var t = path.TrimEnd('\\');
        return t.Length == 0 ? path : t[(t.LastIndexOf('\\') + 1)..];
    }

    /// <summary>
    /// The settings of the archive, with what belongs to this PC kept: the secrets the archive does not carry, and where fpcalc is.
    /// </summary>
    private string MergeSettings(string archiveJson)
    {
        var current = _settings.Load();
        var restored = JsonSerializer.Deserialize<AppSettings>(archiveJson) ?? new AppSettings();
        if (string.IsNullOrWhiteSpace(restored.AcoustIdApiKey) && string.IsNullOrWhiteSpace(restored.MusicBrainzContact))
        {
            restored.AcoustIdApiKey = current.AcoustIdApiKey;
            restored.MusicBrainzContact = current.MusicBrainzContact;
            restored.IdentificationConsentUtc = current.IdentificationConsentUtc;
            restored.IdentificationEnabled = current.IdentificationEnabled;
        }
        restored.FpcalcPath = current.FpcalcPath;
        return JsonSerializer.Serialize(restored, Json);
    }

    internal static string HashFile(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s));
    }
}
