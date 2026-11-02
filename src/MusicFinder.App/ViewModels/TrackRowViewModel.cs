using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MusicFinder.Domain.Enums;
using MusicFinder.Domain.Models;

namespace MusicFinder.App.ViewModels;

/// <summary>A genre chip. Unmapped chips show the raw embedded text in the muted colour so gaps stay visible.</summary>
public sealed record GenreChip(string Text, bool IsMapped);

/// <summary>
/// Display wrapper over a result row. The row can be replaced in place (<see cref="Update"/>) after tags,
/// genres or metadata change, so the grid refreshes without losing selection or scroll position.
/// Play statistics come from the maintained TrackPlayStats row; the badge shows this track's status in the live session.
/// </summary>
public sealed partial class TrackRowViewModel : ObservableObject
{
    [ObservableProperty] private SearchResultRow _row;

    public TrackRowViewModel(SearchResultRow row) => _row = row;

    public void Update(SearchResultRow fresh) => Row = fresh;

    // ===== Find Similar =====

    /// <summary>Set while the grid shows "tracks similar to ..." and says how well this one matches and why.</summary>
    public SimilarTrack? Similar { get; private set; }

    public void SetSimilar(SimilarTrack? similar)
    {
        Similar = similar;
        OnPropertyChanged(nameof(MatchText));
        OnPropertyChanged(nameof(WhyText));
        OnPropertyChanged(nameof(HasHalfDouble));
    }

    /// <summary>The match score, 0 to 100.</summary>
    public string MatchText => Similar is { } s ? s.Score.ToString(CultureInfo.CurrentCulture) : "";

    /// <summary>Why it matched: "same genre (Disco)  ·  BPM +3  ·  adjacent key (8A → 9A)".</summary>
    public string WhyText => Similar is { } s ? string.Join("  ·  ", s.Reasons) : "";

    /// <summary>True when the tempo match used double or half time.</summary>
    public bool HasHalfDouble => Similar?.UsedHalfDoubleTempo == true;

    partial void OnRowChanged(SearchResultRow value) => OnPropertyChanged(string.Empty); // every derived property

    public long Id => Row.Id;
    public string Title => Row.Title;
    public string Artist => Row.Artist ?? "";
    public string? ArtKey => Row.ArtworkCacheKey;
    public IReadOnlyList<string> Genres => Row.Genres;
    public IReadOnlyList<GenreChip> GenreChips => Row.Genres.Select(g => new GenreChip(g, Row.HasMappedGenres)).ToList();
    public IReadOnlyList<string> DjTags => Row.DjTags;
    public string BpmText => Row.Bpm is { } b ? Math.Round(b).ToString("0", CultureInfo.CurrentCulture) : "";
    public string KeyText => Row.MusicalKey ?? "";
    public string DecadeText => Row.Decade is { } d ? $"{d}s" : "";
    public string FullPath => Row.FullPath;

    public string LastPlayedText => Row.LastPlayedUtc is { } at
        ? MusicFinder.Application.Dj.RelativeTime.Format(at, DateTime.UtcNow)
        : "Not played";

    public int Plays => Row.PlayCount;

    /// <summary>Counts spelled out, because "played 3 times" would hide how many of them are only guesses.</summary>
    public string PlayDetailText => Row.PlayCount == 0
        ? "Never played"
        : $"{Row.ConfirmedCount} confirmed, {Row.PossibleCount} possible";

    /// <summary>This track's strongest evidence in the live session, as a short chip. Empty when there is none.</summary>
    public string SessionBadge => Row.SessionStatus switch
    {
        SessionPlayStatus.Exported => "Exported",
        SessionPlayStatus.Possible => "Possible",
        SessionPlayStatus.Confirmed => "Played",
        _ => ""
    };

    public bool HasSessionBadge => Row.SessionStatus != SessionPlayStatus.None;
    public bool IsSessionConfirmed => Row.SessionStatus == SessionPlayStatus.Confirmed;
    public bool IsSessionPossible => Row.SessionStatus == SessionPlayStatus.Possible;
    public bool IsSessionExported => Row.SessionStatus == SessionPlayStatus.Exported;

    /// <summary>The pill as the pop-up shows it: longer than the chip, and honest about how much is known.</summary>
    public string SessionPillText => Row.SessionStatus switch
    {
        SessionPlayStatus.Exported => "Dragged this session",
        SessionPlayStatus.Possible => "Possibly played",
        SessionPlayStatus.Confirmed => "Played this session",
        _ => ""
    };

    public string SessionPillTip => Row.SessionStatus switch
    {
        SessionPlayStatus.Exported => "You dragged this track out in the current session. MusicFinder cannot tell whether it was loaded or played.",
        SessionPlayStatus.Possible => "Dragged out, then nothing else happened for the quiet period: probably played, but this is a guess, not proof.",
        SessionPlayStatus.Confirmed => "Marked as played in the current session (by you or by your DJ software's history).",
        _ => ""
    };

    public string DurationText => Row.DurationMs is { } ms
        ? TimeSpan.FromMilliseconds(ms).ToString(ms >= 3_600_000 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture)
        : "";

    public string SizeText => FormatSize(Row.FileSize);

    public string FormatText
    {
        get
        {
            var ext = Row.Extension.TrimStart('.').ToUpperInvariant();
            return Row.BitrateKbps is { } kbps ? $"{ext} ({kbps} kbps)" : ext;
        }
    }

    public bool IsMissing => Row.Missing;

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KB",
        _ => $"{bytes} B"
    };
}

/// <summary>One line of the details panel's Play History: what was recorded, when, in which session.</summary>
public sealed record PlayHistoryLine(string Label, string When, string Session, bool IsConfirmed, bool Superseded)
{
    public static PlayHistoryLine From(PlayHistoryItem item)
    {
        var local = item.PlayedUtc.ToLocalTime();
        var when = $"{local:dd/MM/yyyy HH:mm}  ·  {MusicFinder.Application.Dj.RelativeTime.Format(item.PlayedUtc, DateTime.UtcNow)}";
        var session = item.SessionName is { Length: > 0 } name ? name : item.SessionId is null ? "No session" : "Session";
        if (item.Superseded) session += "  ·  replaced by a confirmed play";
        return new PlayHistoryLine(item.Label, when, session, item.Status == PlayStatus.Confirmed, item.Superseded);
    }
}
