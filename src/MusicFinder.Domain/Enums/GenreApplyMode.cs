namespace MusicFinder.Domain.Enums;

/// <summary>What the identify window does with MusicBrainz's community genres when a file already has a genre.</summary>
public enum GenreApplyMode
{
    /// <summary>Propose them ticked only for a file with no genre; a genre already there is shown but left unticked.</summary>
    Fill,
    /// <summary>Add the ones not already there after the file's own genres, ticked. Nothing the file has is lost.</summary>
    Add,
    /// <summary>Replace the file's genres with MusicBrainz's, ticked. The old text is shown in the "Now" column until you write.</summary>
    Replace
}
