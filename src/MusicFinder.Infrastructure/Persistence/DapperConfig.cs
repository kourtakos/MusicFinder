using System.Globalization;

namespace MusicFinder.Infrastructure.Persistence;

/// <summary>
/// Timestamp text conventions.
/// <para>
/// A Dapper <c>TypeHandler&lt;DateTime&gt;</c> does NOT take effect: Dapper resolves built-in types such as DateTime before it
/// consults custom handlers, so DateTime parameters are written by Microsoft.Data.Sqlite as <c>yyyy-MM-dd HH:mm:ss[.fffffff]</c>
/// (UTC values, no 'T' or 'Z') and read back with <c>Kind = Unspecified</c>. The M1/M2 columns (Tracks.ModifiedUtc, IndexedUtc)
/// still use that form; it round-trips and compares correctly by ticks.
/// </para>
/// <para>
/// Tables introduced in M3 (sessions, events, plays) and every nullable DateTime are written explicitly with
/// <see cref="ToDb"/> (fixed-width ISO-8601 UTC, so text order is chronological order), and entity setters restore
/// <c>DateTimeKind.Utc</c> on read.
/// </para>
/// </summary>
public static class DapperConfig
{
    /// <summary>"yyyy-MM-ddTHH:mm:ss.fffffffZ": fixed width, UTC, sortable as text.</summary>
    public static string ToDb(DateTime value) =>
        (value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc))
        .ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Parses text written by <see cref="ToDb"/> (or SQLite's default form) as a UTC time.</summary>
    public static DateTime FromDb(string text) =>
        DateTime.SpecifyKind(
            DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
            DateTimeKind.Utc);
}
