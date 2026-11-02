namespace MusicFinder.Domain.Models;

/// <summary>One file inside a backup archive, with the SHA-256 it had when it was written.</summary>
public sealed record BackupPayload(string Name, string Kind, long Size, string Sha256);

public sealed record BackupRoot(long Id, string Path, bool Enabled, int TrackCount);

/// <summary>What a backup contains. Written last into the archive as manifest.json, and checked before any restore.</summary>
public sealed class BackupManifest
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public int SchemaVersion { get; set; }
    public string AppVersion { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public bool Portable { get; set; }
    public bool IncludesSecrets { get; set; }
    public bool IncludesArtwork { get; set; }
    public List<BackupRoot> Roots { get; set; } = [];
    public Dictionary<string, int> Counts { get; set; } = [];
    public List<BackupPayload> Payloads { get; set; } = [];
}

/// <param name="IncludeSecrets">Also store the AcoustID key and the MusicBrainz contact. Off by default: a backup is a file people copy around.</param>
/// <param name="IncludeArtwork">Also store the cover thumbnail cache (it can be rebuilt, so it is off by default).</param>
public sealed record BackupRequest(string TargetPath, bool IncludeSecrets = false, bool IncludeArtwork = false, bool Portable = false);

public sealed record BackupResult(string Path, long Bytes, BackupManifest Manifest);

/// <summary>The archive is damaged, is not a MusicFinder backup, or was made by a newer version of the format.</summary>
public sealed class BackupFormatException(string message) : Exception(message);

/// <summary>"Files that were under OldPrefix are now under NewPrefix" (a drive letter, a folder or a share).</summary>
public sealed record PathMapping(string OldPrefix, string NewPrefix);
