using System.Globalization;

namespace MusicFinder.Application.Dj;

/// <summary>
/// "23 min ago", "1 h 14 min ago", "yesterday 23:47", "26/01/2025". Times are stored in UTC and shown in local time,
/// so the zone is a parameter and tests do not depend on the machine's settings.
/// </summary>
public static class RelativeTime
{
    public static string Format(DateTime utc, DateTime nowUtc, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var age = nowUtc - utc;
        if (age < TimeSpan.FromMinutes(1)) return "just now"; // also covers small clock skew (a time slightly in the future)

        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min ago";

        if (age < TimeSpan.FromHours(24))
        {
            var hours = (int)age.TotalHours;
            var minutes = age.Minutes;
            return minutes == 0 ? $"{hours} h ago" : $"{hours} h {minutes:00} min ago";
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone).Date;
        return local.Date == today.AddDays(-1)
            ? "yesterday " + local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
    }
}
