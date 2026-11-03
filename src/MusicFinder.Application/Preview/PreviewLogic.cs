using System.Globalization;
using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Preview;

/// <summary>The digital timer text: "01:23 / 03:45", hours only when needed, never culture-dependent.</summary>
public static class PreviewTime
{
    public static string Format(TimeSpan elapsed, TimeSpan? total)
    {
        var right = total is { } t && t > TimeSpan.Zero ? One(t) : "--:--";
        return One(elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed) + " / " + right;
    }

    private static string One(TimeSpan value)
    {
        var seconds = (long)value.TotalSeconds;
        var h = seconds / 3600;
        var m = seconds / 60 % 60;
        var s = seconds % 60;
        return h > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{s:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{m:00}:{s:00}");
    }
}

/// <summary>The scrolling line next to the title. Lists only what the file says; nothing is guessed or filled in.</summary>
public static class PreviewCaption
{
    public static string Build(PreviewTrack track)
    {
        var parts = new List<string>();
        Add(parts, track.Artist);
        var album = track.Album?.Trim();
        if (!string.IsNullOrEmpty(album))
            parts.Add(track.Year is > 0 ? $"{album} ({track.Year.Value.ToString(CultureInfo.InvariantCulture)})" : album);
        else if (track.Year is > 0)
            parts.Add(track.Year.Value.ToString(CultureInfo.InvariantCulture));
        Add(parts, track.Genre);
        if (track.Bpm is > 0) parts.Add(Math.Round(track.Bpm.Value).ToString("0", CultureInfo.InvariantCulture) + " BPM");
        Add(parts, track.MusicalKey);

        var format = track.Extension.TrimStart('.').ToUpperInvariant();
        var tech = track.BitrateKbps is > 0
            ? track.BitrateKbps.Value.ToString(CultureInfo.InvariantCulture) + " kbps " + format
            : format;
        if (tech.Length > 0) parts.Add(tech);
        return string.Join(" · ", parts);
    }

    private static void Add(List<string> parts, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) parts.Add(value.Trim());
    }
}

/// <summary>
/// Pure transitions of the preview status. The player shell applies the audio side effects and reports what happened through these.
/// A stopped or finished track stays loaded (state Ended, position zero) so Play replays it.
/// </summary>
public static class PreviewStateMachine
{
    public static PreviewStatus Requested(PreviewStatus current, PreviewTrack track) =>
        new(PreviewState.Loading, TimeSpan.Zero, track.DurationMs is > 0 ? TimeSpan.FromMilliseconds(track.DurationMs.Value) : null, null, track);

    public static PreviewStatus Started(PreviewStatus current, TimeSpan duration) =>
        current with { State = PreviewState.Playing, Duration = duration > TimeSpan.Zero ? duration : current.Duration, Error = null };

    public static PreviewStatus Paused(PreviewStatus current) =>
        current.State == PreviewState.Playing ? current with { State = PreviewState.Paused } : current;

    public static PreviewStatus Resumed(PreviewStatus current) =>
        current.State == PreviewState.Paused ? current with { State = PreviewState.Playing } : current;

    public static PreviewStatus Ended(PreviewStatus current) =>
        current.Track is null ? current : current with { State = PreviewState.Ended, Position = TimeSpan.Zero };

    public static PreviewStatus Stopped(PreviewStatus current) =>
        current.State is PreviewState.Idle ? current : Ended(current);

    public static PreviewStatus Failed(PreviewStatus current, string message) =>
        current with { State = PreviewState.Failed, Error = message, Position = TimeSpan.Zero };

    public static PreviewStatus Moved(PreviewStatus current, TimeSpan position)
    {
        var max = current.Duration ?? position;
        var clamped = position < TimeSpan.Zero ? TimeSpan.Zero : position > max ? max : position;
        return current with { Position = clamped };
    }
}
