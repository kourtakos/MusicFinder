using MusicFinder.Domain.Models;

namespace MusicFinder.Domain.Contracts;

/// <summary>
/// A small listen-before-you-drag player. It is not part of DJ sessions: a preview is not a drag and not a play.
/// Files are opened read-only and shared, so other programs and MusicFinder's own renames are never blocked by it.
/// </summary>
public interface IPreviewPlayer : IDisposable
{
    PreviewStatus Status { get; }

    /// <summary>0 to 1.</summary>
    float Volume { get; set; }

    /// <summary>Windows endpoint id of the output; null means the Windows default. A device that is gone is an error, never a silent fallback.</summary>
    string? DeviceId { get; set; }

    /// <summary>Raised on any thread whenever the state changes.</summary>
    event EventHandler<PreviewStatus>? StateChanged;

    /// <summary>Raised on any thread about four times a second while playing.</summary>
    event EventHandler<TimeSpan>? PositionChanged;

    /// <summary>Starts this track; whatever was playing stops first (the latest request wins). Failures end as <see cref="PreviewState.Failed"/>, not exceptions.</summary>
    Task PlayAsync(PreviewTrack track, CancellationToken ct);

    void Pause();
    void Resume();
    void Stop();
    void Seek(TimeSpan position);

    /// <summary>Stops and lets go of the file when it is the one being previewed. Called before MusicFinder writes tags to it or renames it.</summary>
    void ReleaseIfPlaying(string fullPath);
}

public interface IPreviewOutputDevices
{
    Task<IReadOnlyList<PreviewDevice>> ListAsync(CancellationToken ct);
}
