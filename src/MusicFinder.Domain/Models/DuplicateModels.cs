namespace MusicFinder.Domain.Models;

/// <summary>A file the duplicate scan looks at, with the hash remembered for its current size and modified time (null = not hashed yet).</summary>
public sealed record HashCandidate(long TrackId, string FullPath, long FileSize, DateTime ModifiedUtc, string? Hash);

/// <summary>One file in a group of byte-identical files.</summary>
public sealed record DuplicateMember(
    long TrackId, string FullPath, string Extension, long FileSize, string? Artist, string? Title,
    int? BitrateKbps, string? Codec, int PlayCount, DateTime ModifiedUtc);

/// <summary>
/// Files whose bytes are identical (same SHA-256). <see cref="SuggestedKeepTrackId"/> is only a suggestion, with the reasons;
/// nothing is done to any file because of it.
/// </summary>
public sealed record DuplicateGroup(
    string Hash, long FileSize, IReadOnlyList<DuplicateMember> Members, long SuggestedKeepTrackId, IReadOnlyList<string> KeepReasons)
{
    /// <summary>Space that would be freed if all but one copy were removed (informational).</summary>
    public long ExtraBytes => FileSize * (Members.Count - 1);
}

public sealed record DuplicateScanProgress(int Total, int Done, int Hashed, int Unreadable, string? Current, long BytesHashed);

public sealed record DuplicateScanSummary(
    int Candidates, int AlreadyHashed, int Hashed, int Unreadable, bool Cancelled, int Groups, int FilesInGroups);

public sealed record HashedMember(string Hash, DuplicateMember Member);
