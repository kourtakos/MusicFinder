using System.Text.Json;

namespace MusicFinder.Application.Search;

/// <summary>A genre filter entry: <see cref="Kind"/> 0 = a taxonomy genre (by id), 1 = Unknown genre, 2 = Uncategorized.</summary>
public sealed record SavedGenreRef(int Kind, long Id);

/// <summary>
/// What a saved search remembers: the typed query, the filters and the sort. Not the folder (a saved search looks at the whole
/// library) and never a list of tracks: opening it runs the search again.
/// </summary>
public sealed record SavedSearchState
{
    public int Version { get; init; } = 1;
    public string Text { get; init; } = "";
    public List<SavedGenreRef> Genres { get; init; } = [];
    public List<long> TagIds { get; init; } = [];
    public List<int> Decades { get; init; } = [];
    public double? BpmMin { get; init; }
    public double? BpmMax { get; init; }
    public int? PlayedId { get; init; }
    public bool HideConfirmed { get; init; }
    public bool HidePossible { get; init; }
    public int SortField { get; init; }
    public int SortDirection { get; init; }

    public bool IsEmpty => Text.Length == 0 && Genres.Count == 0 && TagIds.Count == 0 && Decades.Count == 0
        && BpmMin is null && BpmMax is null && PlayedId is null && !HideConfirmed && !HidePossible;

    public string ToJson() => JsonSerializer.Serialize(this);

    /// <summary>Null when the text is not a saved search (damaged or from a newer version): the caller says so instead of guessing.</summary>
    public static SavedSearchState? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var state = JsonSerializer.Deserialize<SavedSearchState>(json);
            return state is { Version: 1 } ? state : null;
        }
        catch (JsonException) { return null; }
    }
}
