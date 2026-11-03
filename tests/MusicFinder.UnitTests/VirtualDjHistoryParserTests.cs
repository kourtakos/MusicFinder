using System.Text;
using MusicFinder.Application.Dj;

namespace MusicFinder.UnitTests;

public class VirtualDjHistoryParserTests
{
    private static readonly DateOnly Day = new(2026, 11, 3);
    private static readonly TimeZoneInfo Athens = TimeZoneInfo.FindSystemTimeZoneById("GTB Standard Time");

    // Shaped like a real VirtualDJ 2026 file (no #EXTM3U header, CRLF, one #EXTVDJ line then the path); paths are sanitised.
    private const string Sample =
        "#EXTVDJ:<time>21:17</time><lastplaytime>1793733460</lastplaytime><filesize>4495565</filesize><artist>Hanni El Khatib</artist><title>I Got a Thing</title><songlength>120.047</songlength>\r\n" +
        "C:\\MF-Fixtures\\Booze\\I Got a Thing - Hanni El Khatib.mp3\r\n" +
        "#EXTVDJ:<time>21:18</time><lastplaytime>1793733522</lastplaytime><filesize>11364900</filesize><artist>Death in Vegas</artist><title>Aisha</title><songlength>352.850</songlength>\r\n" +
        "C:\\MF-Fixtures\\Booze\\Aisha - Death in Vegas.mp3\r\n" +
        "#EXTVDJ:<time>21:21</time><lastplaytime>1793733684</lastplaytime><filesize>4496503</filesize><artist></artist><title>107. Βανδαλούπ - Ο κακός ζητάς (δελτάς   αη - διας)</title><remix>δελτάς   αη - διας</remix><songlength>140.030</songlength>\r\n" +
        "D:\\Other Drive\\Music\\Ελληνικά\\Βανδαλούπ - Ο κακός ζητάς (δελτάς   αη - διας).mp3\r\n" +
        "#EXTVDJ:<time>21:23</time><lastplaytime>1793733800</lastplaytime><filesize>4495565</filesize><artist>Hanni El Khatib</artist><title>I Got a Thing</title><songlength>120.047</songlength>\r\n" +
        "C:\\MF-Fixtures\\Booze\\I Got a Thing - Hanni El Khatib.mp3\r\n";

    private static HistoryParseResult Parse(string text, bool bom = false, DateOnly? day = null, TimeZoneInfo? zone = null)
    {
        var bytes = new UTF8Encoding(bom).GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
        return VirtualDjHistoryParser.Parse(bytes, day ?? Day, zone ?? Athens);
    }

    [Fact]
    public void RealShapedSample_ReadsEveryEntry()
    {
        var result = Parse(Sample);

        Assert.Equal(4, result.Events.Count);
        Assert.Empty(result.Warnings);
        Assert.False(result.HasIncompleteTail);

        var first = result.Events[0];
        Assert.Equal("VirtualDJ", first.Source);
        Assert.Equal(@"C:\MF-Fixtures\Booze\I Got a Thing - Hanni El Khatib.mp3", first.FilePath);
        Assert.Equal("Hanni El Khatib", first.Artist);
        Assert.Equal("I Got a Thing", first.Title);
        Assert.Equal(4495565, first.FileSize);
        Assert.Equal(120.047, first.DurationSeconds);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793733460).UtcDateTime, first.PlayedUtc);
        Assert.Equal(DateTimeKind.Utc, first.PlayedUtc.Kind);
    }

    [Fact]
    public void EmptyArtist_IsNullAndGreekTitleAndRemixAreKept()
    {
        var e = Parse(Sample).Events[2];

        Assert.Null(e.Artist);
        Assert.Equal("107. Βανδαλούπ - Ο κακός ζητάς (δελτάς   αη - διας)", e.Title);   // the three spaces survive
        Assert.Equal("δελτάς   αη - διας", e.Remix);
        Assert.Equal(@"D:\Other Drive\Music\Ελληνικά\Βανδαλούπ - Ο κακός ζητάς (δελτάς   αη - διας).mp3", e.FilePath);
    }

    [Fact]
    public void SameTrackPlayedTwice_GivesTwoEventsWithDistinctKeys()
    {
        var events = Parse(Sample).Events;

        Assert.Equal(events[0].FilePath, events[3].FilePath);
        Assert.NotEqual(events[0].ExternalEventKey, events[3].ExternalEventKey);
        Assert.Equal(4, events.Select(e => e.ExternalEventKey).Distinct().Count());
        Assert.StartsWith("vdj:1793733460:", events[0].ExternalEventKey);
    }

    [Fact]
    public void Key_IsStableAcrossReadsAndIgnoresPathCaseAndSlashStyle()
    {
        var a = Parse(Sample).Events[0].ExternalEventKey;
        var b = Parse(Sample).Events[0].ExternalEventKey;

        Assert.Equal(a, b);
        Assert.Equal(a, VirtualDjHistoryParser.EventKey(1793733460, @"c:/mf-fixtures/booze/i got a thing - hanni el khatib.mp3"));
    }

    [Fact]
    public void HeaderLineAndBlankLinesAreTolerated()
    {
        var result = Parse("#EXTM3U\r\n\r\n" + Sample + "\r\n\r\n");

        Assert.Equal(4, result.Events.Count);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Utf8Bom_IsAccepted()
    {
        var result = Parse(Sample, bom: true);

        Assert.Equal(4, result.Events.Count);
        Assert.Equal("Hanni El Khatib", result.Events[0].Artist);
    }

    [Fact]
    public void Windows1253Bytes_FallBackWithAWarning()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var greek = Encoding.GetEncoding(1253);
        var text = "#EXTVDJ:<time>21:21</time><lastplaytime>1793733684</lastplaytime><artist>Δέσποινα Βανδή</artist><title>Τι κάνω μόνη μου</title>\r\n" +
                   "D:\\Music\\Τι κάνω μόνη μου.mp3\r\n";

        var result = VirtualDjHistoryParser.Parse(greek.GetBytes(text), Day, Athens);

        var e = Assert.Single(result.Events);
        Assert.Equal("Δέσποινα Βανδή", e.Artist);
        Assert.Equal(@"D:\Music\Τι κάνω μόνη μου.mp3", e.FilePath);
        Assert.Contains(result.Warnings, w => w.Contains("1253"));
    }

    [Fact]
    public void HeaderWithoutItsPathYet_IsDeferred()
    {
        var half = Sample + "#EXTVDJ:<time>21:30</time><lastplaytime>1793734200</lastplaytime><artist>X</artist><title>Y</title>\r\n";

        var result = Parse(half);

        Assert.Equal(4, result.Events.Count);
        Assert.True(result.HasIncompleteTail);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void PathLineCutMidWrite_IsDeferred()
    {
        var cut = Sample + "#EXTVDJ:<time>21:30</time><lastplaytime>1793734200</lastplaytime><artist>X</artist><title>Y</title>\r\nC:\\MF-Fixtures\\Boo";   // no newline

        var result = Parse(cut);

        Assert.Equal(4, result.Events.Count);
        Assert.True(result.HasIncompleteTail);
    }

    [Fact]
    public void MissingLastPlayTime_FallsBackToTheLocalTimeOfTheFileDate()
    {
        var text = "#EXTVDJ:<time>21:17</time><artist>A</artist><title>T</title>\r\nC:\\a.mp3\r\n";

        var e = Assert.Single(Parse(text).Events);

        // 3 Nov 2026 21:17 in Athens (UTC+2) is 19:17 UTC.
        Assert.Equal(new DateTime(2026, 11, 3, 19, 17, 0, DateTimeKind.Utc), e.PlayedUtc);
        Assert.StartsWith("vdj:2026-11-03T21:17:", e.ExternalEventKey);
    }

    [Fact]
    public void AmbiguousLocalTime_TakesTheEarlierInstant()
    {
        // 25 Oct 2026 at 04:00 local the Athens clock goes back to 03:00, so 03:30 happens twice.
        var text = "#EXTVDJ:<time>03:30</time><artist>A</artist><title>T</title>\r\nC:\\a.mp3\r\n";

        var e = Assert.Single(Parse(text, day: new DateOnly(2026, 10, 25)).Events);

        Assert.Equal(new DateTime(2026, 10, 25, 0, 30, 0, DateTimeKind.Utc), e.PlayedUtc);   // 03:30 at UTC+3, the first pass
    }

    [Fact]
    public void LocalTimeThatDoesNotExist_IsSkippedWithAWarning()
    {
        // 29 Mar 2026: 03:00 jumps to 04:00 in Athens, so 03:30 never happens.
        var text = "#EXTVDJ:<time>03:30</time><artist>A</artist><title>T</title>\r\nC:\\a.mp3\r\n";

        var result = Parse(text, day: new DateOnly(2026, 3, 29));

        Assert.Empty(result.Events);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void LastPlayTimeThatDisagreesWithTheLocalTime_Warns()
    {
        var text = "#EXTVDJ:<time>21:17</time><lastplaytime>1793733460</lastplaytime><artist>A</artist><title>T</title>\r\nC:\\a.mp3\r\n".Replace("21:17", "09:00");

        var result = Parse(text);

        var e = Assert.Single(result.Events);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793733460).UtcDateTime, e.PlayedUtc);   // lastplaytime still wins
        Assert.Contains(result.Warnings, w => w.Contains("differs"));
    }

    [Fact]
    public void GarbageLines_AreSkippedWithAWarning_AndTheRestStillImports()
    {
        var text = "garbage line\r\n" + Sample;

        var result = Parse(text);

        Assert.Equal(4, result.Events.Count);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void HeaderWithoutAPath_BeforeAnotherHeader_IsReportedAndSkipped()
    {
        var text = "#EXTVDJ:<time>21:00</time><lastplaytime>1793732400</lastplaytime><artist>A</artist><title>T</title>\r\n" + Sample;

        var result = Parse(text);

        Assert.Equal(4, result.Events.Count);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void XmlEntitiesInTags_AreDecoded()
    {
        var text = "#EXTVDJ:<time>21:17</time><lastplaytime>1793733460</lastplaytime><artist>Tom &amp; Jerry</artist><title>A &lt;b&gt;</title>\r\nC:\\a.mp3\r\n";

        var e = Assert.Single(Parse(text).Events);

        Assert.Equal("Tom & Jerry", e.Artist);
        Assert.Equal("A <b>", e.Title);
    }

    [Fact]
    public void EmptyFile_GivesNothing()
    {
        var result = VirtualDjHistoryParser.Parse([], Day, Athens);

        Assert.Empty(result.Events);
        Assert.Empty(result.Warnings);
        Assert.False(result.HasIncompleteTail);
    }

    [Fact]
    public void BrokenNumbers_DoNotCrash()
    {
        var text = "#EXTVDJ:<time>xx</time><lastplaytime>abc</lastplaytime><filesize>-</filesize><songlength>?</songlength><artist>A</artist><title>T</title>\r\nC:\\a.mp3\r\n";

        var result = Parse(text);

        Assert.Empty(result.Events);
        Assert.NotEmpty(result.Warnings);
    }
}
