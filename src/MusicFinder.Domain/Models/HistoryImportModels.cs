namespace MusicFinder.Domain.Models;

/// <summary>
/// One play reported by a DJ program's own history (VirtualDJ, Traktor). It is evidence about the DJ program, not about a track in the library:
/// matching it to a library track is a separate step. <see cref="ExternalEventKey"/> is stable across re-reads of the same file, so importing twice
/// never counts a play twice.
/// </summary>
public sealed record ImportedPlayEvent(
    string Source, string ExternalEventKey, DateTime PlayedUtc, string FilePath,
    string? Artist, string? Title, string? Remix, long? FileSize, double? DurationSeconds);
