using System.Windows;
using System.Windows.Media.Imaging;
using MusicFinder.App.Controls;

namespace MusicFinder.UiSmokeTests;

/// <summary>
/// The thumbnail control sits inside recycled DataGrid rows, so these tests drive it the way recycling does:
/// the same instance gets a new track while an earlier request may still be in flight.
/// </summary>
public class ArtworkThumbnailTests
{
    private static BitmapSource Marker(int pixels)
    {
        var bitmap = BitmapSource.Create(pixels, pixels, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null,
            new byte[pixels * pixels * 4], pixels * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static ArtworkThumbnail Create(FakeArtworkService service)
    {
        var control = new ArtworkThumbnail { Width = 44, Height = 44, Service = service, PixelSize = 64 };
        control.Measure(new Size(44, 44));
        control.Arrange(new Rect(0, 0, 44, 44));
        return control;
    }

    private static Visibility PlaceholderOf(ArtworkThumbnail c) => ((System.Windows.Controls.TextBlock)c.FindName("Placeholder")).Visibility;

    [Fact]
    public Task WithoutAnArtKey_ItShowsThePlaceholder_AndDoesNoWork() => Ui.RunAsync(async () =>
    {
        var service = new FakeArtworkService();
        var control = Create(service);

        control.AudioPath = @"C:\m\a.mp3"; // key stays null and folder art is off
        await Task.Delay(200);

        Assert.Null(control.Shown);
        Assert.Equal(Visibility.Visible, PlaceholderOf(control));
        Assert.Equal(0, service.Requests);
    });

    [Fact]
    public Task WithoutAKey_ButFolderArtEnabled_ItAsksTheService() => Ui.RunAsync(async () =>
    {
        var service = new FakeArtworkService { UseFolderArtwork = true };
        service.Script[""] = (TimeSpan.Zero, Marker(30));
        var control = Create(service);

        control.AudioPath = @"C:\m\a.mp3";
        await Ui.WaitUntilAsync(() => control.Shown is not null, what: "folder art shown");
        Assert.Equal(30, control.Shown!.PixelWidth);
    });

    [Fact]
    public Task LoadsTheCover_HidesThePlaceholder() => Ui.RunAsync(async () =>
    {
        var service = new FakeArtworkService();
        service.Script["k1"] = (TimeSpan.FromMilliseconds(30), Marker(20));
        var control = Create(service);

        control.AudioPath = @"C:\m\a.mp3";
        control.ArtKey = "k1";

        await Ui.WaitUntilAsync(() => control.Shown is not null, what: "cover shown");
        Assert.Equal(20, control.Shown!.PixelWidth);
        Assert.Equal(Visibility.Collapsed, PlaceholderOf(control));
        Assert.Equal(1, service.Requests); // path + key changed together but produced one request
    });

    [Fact]
    public Task AMemoryCacheHit_IsShownWithoutAskingTheService() => Ui.RunAsync(async () =>
    {
        var service = new FakeArtworkService();
        service.Cached["hot"] = Marker(15);
        var control = Create(service);

        control.AudioPath = @"C:\m\a.mp3";
        control.ArtKey = "hot";

        await Ui.WaitUntilAsync(() => control.Shown is not null, what: "cached cover shown");
        Assert.Equal(15, control.Shown!.PixelWidth);
        Assert.Equal(0, service.Requests); // no debounce, no async load: scrolling back over covers never flickers
    });

    [Fact]
    public Task WhenARowIsRecycled_TheOldCoverIsClearedImmediately() => Ui.RunAsync(async () =>
    {
        var service = new FakeArtworkService();
        service.Script["a"] = (TimeSpan.Zero, Marker(11));
        service.Script["b"] = (TimeSpan.FromMilliseconds(400), Marker(22));
        var control = Create(service);
        control.AudioPath = @"C:\m\a.mp3";
        control.ArtKey = "a";
        await Ui.WaitUntilAsync(() => control.Shown is not null);

        control.AudioPath = @"C:\m\b.mp3"; // the container now represents another track
        control.ArtKey = "b";

        Assert.Null(control.Shown);                                  // synchronously: never show track A's cover for track B
        Assert.Equal(Visibility.Visible, PlaceholderOf(control));
        await Ui.WaitUntilAsync(() => control.Shown is not null, what: "new cover");
        Assert.Equal(22, control.Shown!.PixelWidth);
    });

    [Fact]
    public Task FastRecycling_ShowsOnlyTheLatestCover_EvenIfAnEarlierRequestFinishesLast() => Ui.RunAsync(async () =>
    {
        var service = new FakeArtworkService();
        service.Script["slow"] = (TimeSpan.FromMilliseconds(400), Marker(11));  // finishes after the later request
        service.Script["fast"] = (TimeSpan.FromMilliseconds(20), Marker(22));
        var control = Create(service);

        control.AudioPath = @"C:\m\a.mp3";
        control.ArtKey = "slow";
        await Task.Delay(120);                       // let the slow request start...
        control.AudioPath = @"C:\m\b.mp3";
        control.ArtKey = "fast";                     // ...then the row is recycled

        await Ui.WaitUntilAsync(() => control.Shown is not null, what: "latest cover");
        await Task.Delay(600);                       // the stale request completes in the meantime

        Assert.Equal(22, control.Shown!.PixelWidth); // and must have been discarded
    });

    [Fact]
    public Task ScrollingPastARow_BeforeTheDebounce_CostsNothing() => Ui.RunAsync(async () =>
    {
        var service = new FakeArtworkService();
        service.Script["k"] = (TimeSpan.Zero, Marker(9));
        var control = Create(service);

        // A row that is recycled again within the debounce window never reaches the service.
        for (var i = 0; i < 5; i++)
        {
            control.AudioPath = $@"C:\m\{i}.mp3";
            control.ArtKey = "k";
            await Task.Delay(5);
        }
        await Ui.WaitUntilAsync(() => control.Shown is not null, what: "final cover");

        Assert.True(service.Requests <= 2, $"expected coalesced requests, got {service.Requests}");
    });

    [Fact]
    public Task MissingCoverResult_LeavesThePlaceholder() => Ui.RunAsync(async () =>
    {
        var service = new FakeArtworkService();
        service.Script["broken"] = (TimeSpan.Zero, null); // service reports "no usable image"
        var control = Create(service);

        control.AudioPath = @"C:\m\a.mp3";
        control.ArtKey = "broken";
        await Ui.WaitUntilAsync(() => service.Requests == 1, what: "request made");
        await Task.Delay(100);

        Assert.Null(control.Shown);
        Assert.Equal(Visibility.Visible, PlaceholderOf(control));
    });
}
