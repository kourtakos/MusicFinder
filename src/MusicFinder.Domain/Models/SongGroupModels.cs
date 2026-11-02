namespace MusicFinder.Domain.Models;

/// <summary>A present track as the probable-duplicate search sees it. Read from the library data; no file is opened.</summary>
public sealed record SongCandidate(
    long TrackId, string FullPath, string Extension, string? Artist, string? Title, int? DurationMs,
    int? BitrateKbps, string? Codec, int PlayCount);

public enum SongGroupKind
{
    /// <summary>Same artist, title, version and (nearly) the same length: probably the same recording in different files.</summary>
    SameRecording,
    /// <summary>Same song, but a different version (remix, edit, live...) or a clearly different length. Not duplicates.</summary>
    OtherVersions,
}

public sealed record SongMember(
    long TrackId, string FullPath, string Extension, string? Artist, string? Title, int? DurationMs,
    int? BitrateKbps, string? Codec, int PlayCount, string VersionLabel);

public sealed record SongGroup(
    string Key, SongGroupKind Kind, string Heading, IReadOnlyList<SongMember> Members, long SuggestedTrackId, string SuggestionReason, string SearchQuery);

public sealed record ProbableDuplicateResult(IReadOnlyList<SongGroup> SameRecording, IReadOnlyList<SongGroup> OtherVersions);
