using System.Text.Json;
using Microsoft.Data.Sqlite;
using MusicFinder.Domain.Models;

namespace MusicFinder.Infrastructure.Backup;

/// <summary>
/// Swaps a staged restore in. It runs first thing at startup, before any database connection exists, so no file is in use.
/// Every phase is written to the marker before it acts and can be repeated, so a crash or a power cut at any point either finishes
/// on the next start or puts the previous library back. Nothing is deleted: the previous database stays next to the new one.
/// </summary>
public static class RestoreFinalizer
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static RestoreMarker? ReadMarker(AppPaths paths)
    {
        try
        {
            return File.Exists(paths.RestoreMarkerPath)
                ? JsonSerializer.Deserialize<RestoreMarker>(File.ReadAllText(paths.RestoreMarkerPath))
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    public static void WriteMarker(AppPaths paths, RestoreMarker marker)
    {
        Directory.CreateDirectory(paths.DataDir);
        var tmp = paths.RestoreMarkerPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(marker, Json));
        File.Move(tmp, paths.RestoreMarkerPath, overwrite: true);
    }

    /// <param name="phaseHook">Called after each phase has been recorded; tests use it to simulate a crash.</param>
    public static RestoreOutcome? TryApply(AppPaths paths, Action<string>? phaseHook = null)
    {
        if (!File.Exists(paths.RestoreMarkerPath)) return null;
        var marker = ReadMarker(paths);
        if (marker is null)
            return new RestoreOutcome(false, "A pending restore could not be read and was left alone. Your library was not changed.", null, null);

        try
        {
            if (marker.Phase == RestorePhase.Staged)
            {
                if (!File.Exists(marker.StagingDb) || !string.Equals(RestoreService.HashFile(marker.StagingDb), marker.StagingSha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(paths.RestoreMarkerPath);
                    return new RestoreOutcome(false, "The prepared restore was damaged or missing, so it was dropped. Your library was not changed.", null, marker.SafetyBackup);
                }

                CheckpointLive(paths.DatabasePath);
                if (File.Exists(paths.DatabasePath))
                {
                    // The name is recorded before the move, so a crash between the two still knows where the old library went.
                    marker.PreviousDb ??= UniquePath(paths.DatabasePath + ".previous");
                    WriteMarker(paths, marker);
                    Retry(() => File.Move(paths.DatabasePath, marker.PreviousDb));
                }
                DeleteIfExists(paths.DatabasePath + "-wal");
                DeleteIfExists(paths.DatabasePath + "-shm");
                marker.Phase = RestorePhase.LiveMovedAside;
                WriteMarker(paths, marker);
                phaseHook?.Invoke(marker.Phase);
            }

            if (marker.Phase == RestorePhase.LiveMovedAside)
            {
                if (File.Exists(marker.StagingDb)) Retry(() => File.Move(marker.StagingDb, paths.DatabasePath));
                else if (!File.Exists(paths.DatabasePath))
                    return Rollback(paths, marker, "The prepared database vanished.");
                marker.Phase = RestorePhase.DbSwapped;
                WriteMarker(paths, marker);
                phaseHook?.Invoke(marker.Phase);
            }

            if (marker.Phase == RestorePhase.DbSwapped)
            {
                if (!string.IsNullOrEmpty(marker.StagingSettings) && File.Exists(marker.StagingSettings))
                {
                    if (File.Exists(paths.SettingsPath))
                        File.Copy(paths.SettingsPath, UniquePath(paths.SettingsPath + ".previous"));
                    File.Copy(marker.StagingSettings, paths.SettingsPath, overwrite: true);
                }
                File.WriteAllText(paths.RestoreHistoryPath, JsonSerializer.Serialize(
                    new { AppliedUtc = DateTime.UtcNow, marker.PreviousDb, marker.SafetyBackup }, Json));
                File.Delete(paths.RestoreMarkerPath);
                try { Directory.Delete(marker.StagingDirectory, recursive: true); } catch (IOException) { /* harmless leftover */ }
                phaseHook?.Invoke("Done");
            }

            return new RestoreOutcome(true, "The library was restored from the backup. The previous library is kept next to it as " +
                Path.GetFileName(marker.PreviousDb ?? "(none)") + ", and a full safety backup is in the Backups folder.", marker.PreviousDb, marker.SafetyBackup);
        }
        catch (IOException ex) { return Rollback(paths, marker, ex.Message); }
        catch (UnauthorizedAccessException ex) { return Rollback(paths, marker, ex.Message); }
    }

    /// <summary>Puts the previous library back when the new one could not be put in place.</summary>
    private static RestoreOutcome Rollback(AppPaths paths, RestoreMarker marker, string why)
    {
        if (marker.PreviousDb is not null && File.Exists(marker.PreviousDb) && !File.Exists(paths.DatabasePath))
            File.Move(marker.PreviousDb, paths.DatabasePath);
        if (File.Exists(paths.RestoreMarkerPath)) File.Delete(paths.RestoreMarkerPath);
        return new RestoreOutcome(false, "The restore could not be completed (" + why + "). Your previous library is back in place.", null, marker.SafetyBackup);
    }

    /// <summary>Merges the write-ahead log into the main file so the file that is set aside is complete on its own.</summary>
    private static void CheckpointLive(string databasePath)
    {
        if (!File.Exists(databasePath + "-wal")) return;
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false, Mode = SqliteOpenMode.ReadWrite }.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException) { /* an unreadable live database is set aside as it is */ }
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        return path + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>The process that just closed may still hold the file for a moment.</summary>
    private static void Retry(Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { action(); return; }
            catch (IOException) when (attempt < 20) { Thread.Sleep(500); }
        }
    }
}
