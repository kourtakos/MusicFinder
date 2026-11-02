using MusicFinder.Application.Identification;

namespace MusicFinder.UnitTests;

public class UserAgentBuilderTests
{
    private static readonly AppInfo App = new("MusicFinder", "1.2.3");

    [Fact]
    public void ItUsesTheFormMusicBrainzAsksFor() =>
        Assert.Equal("MusicFinder/1.2.3 ( dj@example.com )", UserAgentBuilder.Build(App, "dj@example.com"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithoutAContact_ThereIsNoUserAgent_SoNoRequestIsSent(string? contact) =>
        Assert.Null(UserAgentBuilder.Build(App, contact));

    [Fact]
    public void CharactersThatCouldBreakTheHeader_AreRemoved()
    {
        var ua = UserAgentBuilder.Build(App, "dj@example.com) evil\r\nX-Injected: 1 (");

        Assert.DoesNotContain("\r", ua);
        Assert.DoesNotContain("\n", ua);
        Assert.Equal(1, ua!.Count(c => c == '('));
        Assert.Equal(1, ua.Count(c => c == ')'));
        Assert.StartsWith("MusicFinder/1.2.3 ( dj@example.com", ua);
    }

    [Fact]
    public void AnAbsurdlyLongContact_IsRejected() =>
        Assert.Null(UserAgentBuilder.Build(App, new string('a', 300) + "@example.com"));

    [Fact]
    public void AUrlContact_IsAccepted() =>
        Assert.Equal("MusicFinder/1.2.3 ( https://example.com/dj )", UserAgentBuilder.Build(App, "https://example.com/dj"));
}
