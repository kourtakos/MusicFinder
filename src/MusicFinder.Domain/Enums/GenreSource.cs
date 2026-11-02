namespace MusicFinder.Domain.Enums;

/// <summary>
/// Provenance of a TrackGenres row. The raw embedded genre text stays in Tracks.GenreRaw and is never discarded.
/// </summary>
public enum GenreSource
{
    /// <summary>Derived from the embedded genre tag through the taxonomy and its aliases.</summary>
    Embedded,
    /// <summary>Assigned by the user inside MusicFinder; never touched by remapping.</summary>
    User
}
