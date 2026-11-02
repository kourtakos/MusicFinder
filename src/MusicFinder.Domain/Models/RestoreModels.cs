namespace MusicFinder.Domain.Models;

public enum SchemaVerdict
{
    /// <summary>Same schema as this version of MusicFinder.</summary>
    Current,
    /// <summary>Made by an older version; it is brought up to date inside the staging copy.</summary>
    WillBeUpgraded,
    /// <summary>Made by a newer version of MusicFinder. It cannot be restored here.</summary>
    TooNew,
}

public sealed record RestoreRootPreview(BackupRoot Root, bool ExistsHere);

/// <summary>What restoring an archive would replace. Nothing has been changed when this is shown.</summary>
public sealed record RestorePreview(
    BackupManifest Manifest, SchemaVerdict Schema, IReadOnlyDictionary<string, int> CurrentCounts, IReadOnlyList<RestoreRootPreview> Roots);

public sealed record RestoreRequest(string ArchivePath, IReadOnlyList<PathMapping> Mappings);

/// <summary>A restore that is checked and waiting. The live database is untouched until MusicFinder is restarted.</summary>
public sealed record StagedRestore(string StagingDirectory, string SafetyBackupPath, int TracksRemapped, int RootsRemapped);

/// <summary>What happened to a pending restore at startup (null when there was none).</summary>
public sealed record RestoreOutcome(bool Applied, string Message, string? PreviousDatabase, string? SafetyBackupPath);

/// <summary>The restore could not be prepared, with the reason in plain words. Nothing was changed.</summary>
public sealed class RestoreRefusedException(string message) : Exception(message);
