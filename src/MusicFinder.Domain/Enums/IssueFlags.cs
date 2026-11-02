namespace MusicFinder.Domain.Enums;

/// <summary>
/// Things about a file that look suspect from the stored data alone. They are hints for review, never proof,
/// and nothing is done to a file because of them.
/// </summary>
[Flags]
public enum IssueFlags
{
    None = 0,
    /// <summary>A lossy file with a low bitrate (below 128 kbps; bitrate says nothing about where the audio came from).</summary>
    LowBitrate = 1,
    OddSampleRate = 2,
    /// <summary>The codec the file reports is not the one its extension suggests.</summary>
    CodecMismatch = 4,
    /// <summary>A lossy file much smaller than its bitrate and duration imply (possibly cut short), or much larger.</summary>
    SizeMismatch = 8,
    ZeroDuration = 16,
    /// <summary>The tags could not be read from this file.</summary>
    Unreadable = 32,
    /// <summary>A tag text that looks like UTF-8 that was read as a single-byte code page.</summary>
    Mojibake = 64
}
