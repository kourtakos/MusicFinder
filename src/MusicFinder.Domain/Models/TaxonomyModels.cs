using MusicFinder.Domain.Entities;

namespace MusicFinder.Domain.Models;

/// <summary>A genre with the number of present tracks in it or its sub-genres, and its children.</summary>
public sealed record GenreNode(Genre Genre, int TrackCount, IReadOnlyList<GenreNode> Children);

/// <summary>An embedded genre value that matches nothing in the taxonomy, with how many tracks carry it.</summary>
public sealed record UnmappedGenre(string Value, int TrackCount);

public sealed record DjTagInfo(CustomTag Tag, int TrackCount);

/// <summary>A validation failure the UI can show verbatim (duplicate name, cycle, genre still has children, ...).</summary>
public sealed class TaxonomyException : Exception
{
    public TaxonomyException(string message) : base(message) { }
}
