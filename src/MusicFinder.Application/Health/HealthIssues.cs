using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Health;

/// <summary>What each finding is called, and the search that lists exactly those tracks in the Library.</summary>
public static class HealthIssues
{
    /// <summary>A search text for the Library; null when there is no such search (the files are gone from the list).</summary>
    public static string? SearchFor(HealthIssueKind kind) => kind switch
    {
        HealthIssueKind.MissingTitle => "missing:title",
        HealthIssueKind.MissingArtist => "missing:artist",
        HealthIssueKind.MissingAlbum => "missing:album",
        HealthIssueKind.MissingGenre => "missing:genre",
        HealthIssueKind.MissingYear => "missing:year",
        HealthIssueKind.MissingBpm => "missing:bpm",
        HealthIssueKind.MissingKey => "missing:key",
        HealthIssueKind.MissingArtwork => "missing:art",
        HealthIssueKind.Mojibake => "issue:mojibake",
        HealthIssueKind.Unreadable => "issue:error",
        HealthIssueKind.LowBitrate => "issue:lowbitrate",
        HealthIssueKind.OddSampleRate => "issue:samplerate",
        HealthIssueKind.CodecMismatch => "issue:mismatch",
        HealthIssueKind.SizeMismatch => "issue:size",
        HealthIssueKind.ZeroDuration => "issue:duration",
        _ => null
    };

    public static string Title(HealthIssueKind kind) => kind switch
    {
        HealthIssueKind.MissingFiles => "Files not found",
        HealthIssueKind.NotChecked => "Not checked yet",
        HealthIssueKind.MissingTitle => "No title",
        HealthIssueKind.MissingArtist => "No artist",
        HealthIssueKind.MissingAlbum => "No album",
        HealthIssueKind.MissingGenre => "No genre",
        HealthIssueKind.MissingYear => "No year",
        HealthIssueKind.MissingBpm => "No BPM",
        HealthIssueKind.MissingKey => "No key",
        HealthIssueKind.MissingArtwork => "No embedded cover",
        HealthIssueKind.Mojibake => "Garbled text",
        HealthIssueKind.Unreadable => "Tags unreadable",
        HealthIssueKind.LowBitrate => "Low bitrate",
        HealthIssueKind.OddSampleRate => "Unusual sample rate",
        HealthIssueKind.CodecMismatch => "Codec differs from extension",
        HealthIssueKind.SizeMismatch => "Size does not fit the bitrate",
        HealthIssueKind.ZeroDuration => "No duration",
        _ => kind.ToString()
    };

    /// <summary>One plain sentence on what the finding means and how sure it is.</summary>
    public static string Explain(HealthIssueKind kind) => kind switch
    {
        HealthIssueKind.MissingFiles => "The library knows these files but they were not found at the last scan (a disconnected drive, or moved or deleted outside MusicFinder). Nothing is removed from the library.",
        HealthIssueKind.NotChecked => "Indexed by an older version; the next scan reads them again and fills in the newer facts.",
        HealthIssueKind.MissingBpm => "BPM and key are never looked up online; analyse them from the audio or type them in the tag editor.",
        HealthIssueKind.MissingArtwork => "Only covers embedded in the file are counted; a cover.jpg beside the file is not.",
        HealthIssueKind.Mojibake => "Text that looks like UTF-8 read as a single-byte code page (Greek turned into accented Latin letters, for example). A heuristic: look before you fix.",
        HealthIssueKind.Unreadable => "The tags could not be read; the file is still listed by its name. It may be damaged or in an unusual format.",
        HealthIssueKind.LowBitrate => "Lossy files below 128 kbps. Bitrate alone says nothing about where a recording came from.",
        HealthIssueKind.OddSampleRate => "A sample rate that is not one of the usual ones; worth a look, not necessarily wrong.",
        HealthIssueKind.CodecMismatch => "The audio inside is not the kind the file extension suggests.",
        HealthIssueKind.SizeMismatch => "The file is much smaller or larger than its bitrate and length imply; a smaller file may be cut short. A hint, not proof.",
        HealthIssueKind.ZeroDuration => "No length could be read from the file.",
        _ => ""
    };
}
