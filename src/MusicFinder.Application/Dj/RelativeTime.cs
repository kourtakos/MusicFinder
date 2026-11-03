using System.Globalization;

namespace MusicFinder.Application.Dj;

/// <summary>
/// "23 min ago", "1 h 14 min ago", "yesterday 23:47", "26/01/2025". Times are stored in UTC and shown in local time,
/// so the zone is a parameter and tests do not depend on the machine's settings.
/// The wording comes from <see cref="Texts"/>, which the app replaces with the user's language at start-up.
/// </summary>
public static class RelativeTime
{
    /// <summary>The words used. {0} is a number (or the time of day for <see cref="Yesterday"/>), {1} the minutes of "h min".</summary>
    public sealed record Wording(string JustNow, string MinutesAgo, string HoursAgo, string HoursMinutesAgo, string Yesterday);

    public static Wording Texts { get; set; } = new("just now", "{0} min ago", "{0} h ago", "{0} h {1:00} min ago", "yesterday {0}");

    public static string Format(DateTime utc, DateTime nowUtc, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var age = nowUtc - utc;
        if (age < TimeSpan.FromMinutes(1)) return Texts.JustNow; // also covers small clock skew (a time slightly in the future)

        if (age < TimeSpan.FromHours(1)) return string.Format(CultureInfo.InvariantCulture, Texts.MinutesAgo, (int)age.TotalMinutes);

        if (age < TimeSpan.FromHours(24))
        {
            var hours = (int)age.TotalHours;
            var minutes = age.Minutes;
            return minutes == 0
                ? string.Format(CultureInfo.InvariantCulture, Texts.HoursAgo, hours)
                : string.Format(CultureInfo.InvariantCulture, Texts.HoursMinutesAgo, hours, minutes);
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone).Date;
        return local.Date == today.AddDays(-1)
            ? string.Format(CultureInfo.InvariantCulture, Texts.Yesterday, local.ToString("HH:mm", CultureInfo.InvariantCulture))
            : local.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
    }
}
