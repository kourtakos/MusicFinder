namespace MusicFinder.Domain.Models;

public enum PreviewState { Idle, Loading, Playing, Paused, Ended, Failed }

/// <summary>What the preview player needs to know about a track: where it is and what to show.</summary>
public sealed record PreviewTrack(
    long Id, string FullPath, string Title, string? Artist, string? Album, int? Year, string? Genre,
    double? Bpm, string? MusicalKey, long? DurationMs, int? BitrateKbps, string Extension, string? ArtKey);

public sealed record PreviewStatus(PreviewState State, TimeSpan Position, TimeSpan? Duration, string? Error, PreviewTrack? Track)
{
    public static PreviewStatus Idle { get; } = new(PreviewState.Idle, TimeSpan.Zero, null, null, null);
}

/// <summary>An audio output the preview can use. <see cref="Id"/> is the Windows endpoint id.</summary>
public sealed record PreviewDevice(string Id, string Name, bool IsDefault);
