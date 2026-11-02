namespace MusicFinder.Domain.Models;

/// <summary>
/// SQLite stores times as text, and what Microsoft.Data.Sqlite hands back depends on the text: a value written with a
/// trailing "Z" arrives converted to LOCAL time, one without arrives as "Unspecified" but already UTC. Re-labelling a
/// converted value as UTC would shift it by the machine's offset, so normalize by Kind.
/// </summary>
public static class UtcTime
{
    public static DateTime Normalize(DateTime value) =>
        value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    public static DateTime? Normalize(DateTime? value) => value is { } v ? Normalize(v) : null;
}
