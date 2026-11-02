using MusicFinder.Domain.Enums;

namespace MusicFinder.Domain.Models;

public sealed record SearchFilters
{
    /// <summary>Taxonomy genres; selecting a parent also matches all its descendants. OR within the facet.</summary>
    public IReadOnlyList<long> GenreIds { get; init; } = [];
    /// <summary>Tracks with no embedded genre text. Part of the genre facet (OR).</summary>
    public bool UnknownGenre { get; init; }
    /// <summary>Tracks that have genre text but map to no taxonomy genre. Part of the genre facet (OR).</summary>
    public bool Uncategorized { get; init; }
    /// <summary>Custom DJ tags; OR within the facet.</summary>
    public IReadOnlyList<long> TagIds { get; init; } = [];
    public double? BpmMin { get; init; }
    public double? BpmMax { get; init; }
    /// <summary>Decade start years (1980, 1990, ...); OR within the facet.</summary>
    public IReadOnlyList<int> Decades { get; init; } = [];
    public IReadOnlyList<string> Extensions { get; init; } = [];
    public string? MusicalKey { get; init; }
    public bool IncludeMissing { get; init; }
    /// <summary>The "Played" pill. Combines (AND) with the query's own <c>played:</c> filter.</summary>
    public PlayedFilter? Played { get; init; }
    /// <summary>Hide tracks already confirmed played in the live session (needs <see cref="SearchQuery.SessionId"/>).</summary>
    public bool HideConfirmedInSession { get; init; }
    /// <summary>Hide tracks possibly played in the live session (needs <see cref="SearchQuery.SessionId"/>).</summary>
    public bool HidePossibleInSession { get; init; }
}

/// <summary>Opaque keyset cursor: the sort key and id of the last row of the previous page.</summary>
public sealed record SearchCursor(string? SortValue, long LastId);

public sealed record SearchQuery
{
    public string Text { get; init; } = "";
    public long? FolderId { get; init; }
    public bool IncludeSubfolders { get; init; } = true;
    /// <summary>When true the folder scope is ignored.</summary>
    public bool GlobalScope { get; init; }
    public SearchFilters Filters { get; init; } = new();
    /// <summary>The live DJ session, or null. Drives <c>played:session</c>, the hide-in-session filters and each row's session status.</summary>
    public long? SessionId { get; init; }
    public SortField Sort { get; init; } = SortField.Title;
    public SortDirection Direction { get; init; } = SortDirection.Asc;
    public int PageSize { get; init; } = 200;
    public SearchCursor? After { get; init; }
}

public sealed record SearchResultRow(
    long Id, long FolderId, string FullPath, string FileName, string Extension, long FileSize,
    string Title, string? Artist, string? Album, string? GenreRaw, int? Year,
    long? DurationMs, int? BitrateKbps, double? Bpm, string? MusicalKey, bool Missing,
    string? ArtworkCacheKey = null, IReadOnlyList<string>? MappedGenres = null, IReadOnlyList<string>? Tags = null,
    DateTime? LastPlayedUtc = null, DateTime? LastConfirmedUtc = null, DateTime? LastPossibleUtc = null,
    int ConfirmedCount = 0, int PossibleCount = 0, SessionPlayStatus SessionStatus = SessionPlayStatus.None)
{
    public int PlayCount => ConfirmedCount + PossibleCount;

    private static readonly char[] GenreSeparators = [';', '/', ',', '\\'];

    /// <summary>The genre text as embedded in the file, split into values. Never discarded.</summary>
    public IReadOnlyList<string> RawGenres { get; } = (GenreRaw ?? "")
        .Split(GenreSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Taxonomy genres when the track is mapped, otherwise the raw values (so unmapped genres stay visible).</summary>
    public IReadOnlyList<string> Genres { get; } = MappedGenres is { Count: > 0 }
        ? MappedGenres
        : (GenreRaw ?? "").Split(GenreSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public bool HasMappedGenres => MappedGenres is { Count: > 0 };

    public IReadOnlyList<string> DjTags { get; } = Tags ?? [];

    public int? Decade => Year is > 0 ? Year / 10 * 10 : null;
}

/// <summary>One page of results. <see cref="Warnings"/> carries non-blocking query validation messages.</summary>
public sealed record SearchPage(
    IReadOnlyList<SearchResultRow> Items, SearchCursor? Next, bool HasMore, IReadOnlyList<string> Warnings);
