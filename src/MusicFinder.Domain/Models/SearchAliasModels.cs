namespace MusicFinder.Domain.Models;

/// <summary>
/// The text of one track prepared for smart Greek search. Fields ending in N are normalized (case, accents, final sigma);
/// fields ending in K are the phonetic key of the same text (so Greek and Greeklish spellings meet). <see cref="Extra"/> holds the
/// user's own search aliases that apply to this track.
/// </summary>
public sealed record TrackSearchAlias(
    long TrackId,
    string TitleN, string ArtistN, string AlbumN, string GenreN, string FileN,
    string TitleK, string ArtistK, string AlbumK, string GenreK, string FileK,
    string Extra);

/// <summary>A user-defined equivalence for names the automatic rules cannot bridge, e.g. "Daft Punk" and "νταφτ πανκ".</summary>
public sealed record SearchAliasOverride(long Id, string Source, string Alias, string SourceNormalized, string AliasNormalized);

/// <summary>The stored text a track's alias row is built from; also used to check that the text did not change while the row was prepared.</summary>
public sealed record SearchAliasStaleRow(
    long Id, string FullPath, string FileName, string? Title, string? Artist, string? AlbumArtist, string? Album, string? GenreRaw);

/// <summary>One way a typed word is searched: what kind of form it is and its text.</summary>
public sealed record ExplainedForm(string Kind, string Value);

public sealed record ExplainedTerm(string? Field, bool IsPhrase, IReadOnlyList<ExplainedForm> Forms);

/// <summary>A result with the tier it matched at: 0 as typed, 1 normalized, 2 other keyboard layout, 3 phonetic key, 4 typo.</summary>
public sealed record ExplainedResult(long TrackId, string Title, string? Artist, int Tier, bool InTitleArtistAlbum);

/// <summary>What the search did with a query: the forms it looked for and why each result matched. For tests and the Settings page.</summary>
public sealed record SearchExplanation(
    string Text, IReadOnlyList<string> Warnings, IReadOnlyList<ExplainedTerm> Terms, IReadOnlyList<ExplainedResult> Results, int ResultLimit);

public sealed record SearchAliasResult(bool Success, string? Error, SearchAliasOverride? Alias);
