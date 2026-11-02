using MusicFinder.Application.Dj;

namespace MusicFinder.UnitTests;

public class RelativeTimeTests
{
    private static readonly DateTime Now = new(2026, 11, 1, 20, 0, 0, DateTimeKind.Utc);

    // Athens is UTC+2 on 1 Nov 2026 (EET), so "now" is 22:00 local.
    private static readonly TimeZoneInfo Athens = TimeZoneInfo.FindSystemTimeZoneById("GTB Standard Time");

    private static string Fmt(TimeSpan ago) => RelativeTime.Format(Now - ago, Now, Athens);

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(30, "just now")]
    [InlineData(59, "just now")]
    [InlineData(60, "1 min ago")]
    [InlineData(23 * 60, "23 min ago")]
    [InlineData(59 * 60 + 59, "59 min ago")]
    public void UnderAnHour_ShowsMinutes(int seconds, string expected) => Assert.Equal(expected, Fmt(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(1, 14, "1 h 14 min ago")]   // the mockup's own examples
    [InlineData(2, 3, "2 h 03 min ago")]
    [InlineData(3, 21, "3 h 21 min ago")]
    [InlineData(1, 0, "1 h ago")]
    [InlineData(23, 59, "23 h 59 min ago")]
    public void UnderADay_ShowsHoursAndMinutes(int hours, int minutes, string expected) =>
        Assert.Equal(expected, Fmt(new TimeSpan(hours, minutes, 0)));

    [Fact]
    public void YesterdayInLocalTime_ShowsTheLocalClockTime()
    {
        // 25 h ago = 31 Oct 19:00 UTC = 21:00 local; local "today" is 1 Nov, so it was yesterday.
        Assert.Equal("yesterday 21:00", Fmt(TimeSpan.FromHours(25)));
    }

    [Fact]
    public void OlderThanYesterday_ShowsTheLocalDate()
    {
        Assert.Equal("29/10/2026", Fmt(TimeSpan.FromDays(3)));
        Assert.Equal("26/01/2026", RelativeTime.Format(new DateTime(2026, 1, 26, 1, 14, 0, DateTimeKind.Utc), Now, Athens));
    }

    [Fact]
    public void TheLocalDateIsUsed_NotTheUtcDate()
    {
        // 31 Oct 22:30 UTC is already 1 Nov 00:30 in Athens, so it is the same local day as "now" would be... but older than 24 h
        // is impossible here; use a time 47 h ago: 30 Oct 21:00 UTC = 30 Oct 23:00 local (two local days ago).
        Assert.Equal("30/10/2026", Fmt(TimeSpan.FromHours(47)));
    }

    [Fact]
    public void ATimeSlightlyInTheFuture_ClockSkew_ReadsAsJustNow()
    {
        Assert.Equal("just now", RelativeTime.Format(Now.AddSeconds(5), Now, Athens));
        Assert.Equal("just now", RelativeTime.Format(Now.AddHours(3), Now, Athens));
    }
}
