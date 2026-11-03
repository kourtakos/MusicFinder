using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using MusicFinder.App.Controls;
using MusicFinder.App.ViewModels;
using MusicFinder.App.Views;
using MusicFinder.Application.Preview;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.UiSmokeTests;

/// <summary>The discreet player through the real window. The audio side is a fake: no test here makes a sound.</summary>
public class PreviewUiTests
{
    private sealed class FakePlayer : IPreviewPlayer
    {
        private PreviewStatus _status = PreviewStatus.Idle;
        public List<PreviewTrack> Played { get; } = [];
        public List<string> Calls { get; } = [];
        public List<string> Released { get; } = [];
        public PreviewStatus Status => _status;
        public float Volume { get; set; } = 0.5f;
        public string? DeviceId { get; set; }
        public event EventHandler<PreviewStatus>? StateChanged;
        public event EventHandler<TimeSpan>? PositionChanged;

        public Task PlayAsync(PreviewTrack track, CancellationToken ct)
        {
            Played.Add(track);
            Set(PreviewStateMachine.Requested(_status, track));
            return Task.CompletedTask;
        }

        public void Begin(TimeSpan duration) => Set(PreviewStateMachine.Started(_status, duration));
        public void Fail(string message) => Set(PreviewStateMachine.Failed(_status, message));
        public void Finish() => Set(PreviewStateMachine.Ended(_status));
        public void Move(TimeSpan position) { _status = PreviewStateMachine.Moved(_status, position); PositionChanged?.Invoke(this, position); }

        public void Pause() { Calls.Add("pause"); Set(PreviewStateMachine.Paused(_status)); }
        public void Resume() { Calls.Add("resume"); Set(PreviewStateMachine.Resumed(_status)); }
        public void Stop() { Calls.Add("stop"); Set(PreviewStateMachine.Stopped(_status)); }
        public void Seek(TimeSpan position) { Calls.Add("seek " + (int)position.TotalSeconds); Move(position); }
        public void ReleaseIfPlaying(string fullPath) => Released.Add(fullPath);
        public void Dispose() => Calls.Add("dispose");

        private void Set(PreviewStatus status) { _status = status; StateChanged?.Invoke(this, status); }
    }

    private sealed class FakeDevices : IPreviewOutputDevices
    {
        public List<PreviewDevice> Devices { get; } =
        [
            new("speakers-id", "Speakers (Realtek)", true),
            new("phones-id", "Headphones (USB Audio)", false),
        ];

        public Task<IReadOnlyList<PreviewDevice>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<PreviewDevice>>(Devices.ToList());
    }

    private static readonly (string, string, string, string)[] Files =
    [
        ("a.mp3", "Alpha", "Artist A", "Disco"),
        ("b.mp3", "Bravo", "Artist B", "Funk"),
    ];

    private sealed record Ctx(UiTestApp App, FakePlayer Player, FakeDevices Devices, DataGrid Grid)
    {
        public PreviewViewModel Preview => App.Main.Preview;
        public TrackRowViewModel Row(string title) => App.Main.Library.Search.Results.Single(r => r.Title == title);

        public void DoubleClick(DependencyObject target)
        {
            // MouseDoubleClick is a direct event: in the running app every control in the chain raises its own, with the clicked element as source.
            Grid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = Control.MouseDoubleClickEvent,
                Source = target,
            });
        }

        public DataGridRow RowContainer(string title) => (DataGridRow)Grid.ItemContainerGenerator.ContainerFromItem(Row(title));
    }

    private static async Task<Ctx> StartAsync(Action<MusicFinder.Domain.Models.AppSettings>? settings = null)
    {
        var player = new FakePlayer();
        var devices = new FakeDevices();
        var app = await UiTestApp.CreateWithFilesAsync(s =>
        {
            s.AddSingleton<IPreviewPlayer>(player);
            s.AddSingleton<IPreviewOutputDevices>(devices);
        }, Files);
        if (settings is not null) { var s = app.Settings.Load(); settings(s); app.Settings.Save(s); }
        app.Window.Show();
        await app.Main.InitializeAsync();
        await Ui.WaitUntilAsync(() => app.Main.Library.Search.Results.Count == 2, what: "indexed rows");
        await Task.Delay(200);
        var grid = Ui.FindDescendant<DataGrid>(app.Window) ?? throw new InvalidOperationException("TrackGrid not found");
        return new Ctx(app, player, devices, grid);
    }

    private static Visibility BarVisibility(Ctx c) => Ui.FindDescendant<PreviewBar>(c.App.Window)!.Strip.Visibility;

    [Fact]
    public Task DoubleClickOnARow_PlaysThatFile_ShowsTheBar_AndNeverTouchesTheSession() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        Assert.Equal(Visibility.Collapsed, BarVisibility(c));                       // nothing yet: no bar

        c.DoubleClick(c.RowContainer("Bravo"));

        var played = Assert.Single(c.Player.Played);
        Assert.Equal(Path.Combine(c.App.MusicDir, "b.mp3"), played.FullPath);
        await Ui.WaitUntilAsync(() => c.Preview.Title == "Bravo", what: "title");
        Assert.Contains("Artist B", c.Preview.Caption);
        await Ui.WaitUntilAsync(() => BarVisibility(c) == Visibility.Visible, what: "bar");
        Assert.Null(c.App.Service<ISessionService>().Active);                       // a preview is not a drag and not a play
        Assert.Equal(Path.Combine(c.App.MusicDir, "b.mp3"), c.Preview.AudioPath);
    });

    [Fact]
    public Task DoubleClickOnAHeader_OrOutsideRows_DoesNotPlay() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var header = Ui.FindDescendant<System.Windows.Controls.Primitives.DataGridColumnHeader>(c.Grid)!;

        c.DoubleClick(header);

        Assert.Empty(c.Player.Played);
        Assert.Equal(Visibility.Collapsed, BarVisibility(c));
    });

    [Fact]
    public Task TheTimer_FollowsThePosition_AndTheProgressLineMoves() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.DoubleClick(c.RowContainer("Alpha"));

        c.Player.Begin(TimeSpan.FromSeconds(225));
        c.Player.Move(TimeSpan.FromSeconds(83));
        await Ui.WaitUntilAsync(() => c.Preview.TimeText == "01:23 / 03:45", what: "timer");

        Assert.InRange(c.Preview.Progress, 0.36, 0.38);
        Assert.True(c.Preview.IsPlaying);
        Assert.True(c.Preview.IsScrolling);
        Assert.Equal("", c.Preview.PlayGlyph);                                 // pause glyph while playing

        c.Player.Finish();
        await Ui.WaitUntilAsync(() => c.Preview.State == PreviewState.Ended, what: "ended");
        Assert.Equal("00:00 / 03:45", c.Preview.TimeText);
        Assert.False(c.Preview.IsScrolling);
        Assert.Equal("", c.Preview.PlayGlyph);
    });

    [Fact]
    public Task PlayPauseStopAndSeek_GoToThePlayer() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.DoubleClick(c.RowContainer("Alpha"));
        c.Player.Begin(TimeSpan.FromSeconds(200));

        c.Preview.TogglePlayPauseCommand.Execute(null);
        Assert.Equal(PreviewState.Paused, c.Player.Status.State);
        c.Preview.TogglePlayPauseCommand.Execute(null);
        Assert.Equal(PreviewState.Playing, c.Player.Status.State);
        c.Preview.SeekToFraction(0.5);
        Assert.Contains("seek 100", c.Player.Calls);
        c.Preview.StopCommand.Execute(null);
        Assert.Equal(PreviewState.Ended, c.Player.Status.State);

        c.Preview.TogglePlayPauseCommand.Execute(null);                              // Play on a finished track plays it again
        Assert.Equal(2, c.Player.Played.Count);
        Assert.Equal(c.Player.Played[0], c.Player.Played[1]);
    });

    [Fact]
    public Task Space_TogglesALoadedPreview_ButNeverStartsSound() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;

        Assert.False(c.Preview.TogglePauseIfLoaded());                               // nothing loaded: Space does nothing
        Assert.Empty(c.Player.Played);

        c.DoubleClick(c.RowContainer("Alpha"));
        c.Player.Begin(TimeSpan.FromSeconds(60));
        Assert.True(c.Preview.TogglePauseIfLoaded());
        Assert.Equal(PreviewState.Paused, c.Player.Status.State);
        Assert.True(c.Preview.TogglePauseIfLoaded());
        Assert.Equal(PreviewState.Playing, c.Player.Status.State);

        c.Player.Finish();
        await Ui.WaitUntilAsync(() => c.Preview.State == PreviewState.Ended, what: "ended");
        Assert.False(c.Preview.TogglePauseIfLoaded());                               // a finished track is not restarted by Space
        Assert.Single(c.Player.Played);
    });

    [Fact]
    public Task AFailedPreview_ShowsAShortMessageInTheBar_NotADialog() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.DoubleClick(c.RowContainer("Alpha"));

        c.Player.Fail("The file no longer exists.");
        await Ui.WaitUntilAsync(() => c.Preview.HasError, what: "error");

        var bar = Ui.FindDescendant<PreviewBar>(c.App.Window)!;
        Assert.Equal(Visibility.Visible, bar.ErrorText.Visibility);
        Assert.Equal(Visibility.Collapsed, bar.CaptionText.Visibility);
        Assert.Equal("The file no longer exists.", bar.ErrorText.Text);
    });

    [Fact]
    public Task Dismiss_StopsAndHidesTheBar_AndANewDoubleClickBringsItBack() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.DoubleClick(c.RowContainer("Alpha"));
        c.Player.Begin(TimeSpan.FromSeconds(60));

        c.Preview.DismissCommand.Execute(null);

        Assert.Contains("stop", c.Player.Calls);
        Assert.Equal(Visibility.Collapsed, BarVisibility(c));

        c.DoubleClick(c.RowContainer("Bravo"));
        await Ui.WaitUntilAsync(() => BarVisibility(c) == Visibility.Visible, what: "bar back");
    });

    [Fact]
    public Task TheBar_AndTheSettingsPage_HaveNoBrokenBinding() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.DoubleClick(c.RowContainer("Alpha"));
        c.Player.Begin(TimeSpan.FromSeconds(60));
        await Task.Delay(300);
        Assert.Empty(BindingAudit.FindBroken(Ui.FindDescendant<PreviewBar>(c.App.Window)!));

        c.Player.Fail("The chosen preview output is not available. Pick another in Settings.");
        await Task.Delay(200);
        Assert.Empty(BindingAudit.FindBroken(Ui.FindDescendant<PreviewBar>(c.App.Window)!));

        c.App.Main.SelectedTab = AppTab.Settings;
        await Task.Delay(400);
        Assert.Empty(BindingAudit.FindBroken(c.App.Window));
    });

    [Fact]
    public Task TheOutputChoice_IsSavedAndAppliedToThePlayer_AndAMissingOneIsNeverReplacedByTheDefault() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        await c.Preview.RefreshDevicesAsync();

        Assert.Equal(["Windows default output", "Speakers (Realtek) (Windows default)", "Headphones (USB Audio)"], c.Preview.Devices.Select(d => d.Label));
        Assert.Null(c.Player.DeviceId);
        Assert.True(c.Preview.DeviceIsDefault);                                      // shown as a warning in the bar

        c.Preview.SelectedDevice = c.Preview.Devices.Single(d => d.Id == "phones-id");

        Assert.Equal("phones-id", c.Player.DeviceId);
        Assert.Equal("phones-id", c.App.Settings.Load().PreviewDeviceId);
        Assert.False(c.Preview.DeviceIsDefault);

        c.Devices.Devices.RemoveAll(d => d.Id == "phones-id");                       // unplugged
        await c.Preview.RefreshDevicesAsync();
        Assert.Equal("phones-id", c.Player.DeviceId);                                // still the chosen one, not the default
        Assert.Contains(c.Preview.Devices, d => d.Id == "phones-id" && d.Label.Contains("not connected"));
        Assert.Contains("phones-id", c.App.Settings.Load().PreviewDeviceId);
    });

    [Fact]
    public Task TheVolume_StartsAtHalf_IsAppliedAndRemembered() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        Assert.Equal(0.5, c.Preview.Volume);
        Assert.Equal(0.5f, c.Player.Volume);

        c.Preview.Volume = 0.2;

        Assert.Equal(0.2f, c.Player.Volume);
        await Ui.WaitUntilAsync(() => Math.Abs(c.App.Settings.Load().PreviewVolume - 0.2) < 0.001, what: "volume saved");
    });

    [Fact]
    public Task SavedSettings_AreAppliedAtStart() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync(s => { s.PreviewVolume = 0.8; s.PreviewDeviceId = "phones-id"; });
        await using var _ = c.App;

        // The view model was built from the settings that existed when it was first resolved, which is before the test changed them;
        // so check the production path with a fresh instance.
        var fresh = new PreviewViewModel(c.Player, c.Devices, c.App.Settings, c.App.Service<MusicFinder.App.Services.Artwork.IArtworkService>());

        Assert.Equal(0.8, fresh.Volume);
        Assert.Equal("phones-id", c.Player.DeviceId);
    });

    [Fact]
    public Task ReleaseIfPlaying_IsForwardedForEveryPath() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;

        c.Preview.ReleaseIfPlaying(["x.mp3", "y.mp3"]);

        Assert.Equal(["x.mp3", "y.mp3"], c.Player.Released);
    });

    [Fact]
    public Task TheCaption_ScrollsOnlyWhenItDoesNotFit_AndOnlyWhilePlaying() => Ui.RunAsync(async () =>
    {
        var marquee = new MarqueeText { Width = 120, Text = "Short" };
        var window = new Window { Content = marquee, Width = 300, Height = 100, ShowInTaskbar = false };
        window.Show();
        try
        {
            await Task.Delay(150);
            Assert.False(marquee.IsOverflowing);

            marquee.Text = "Artist · Album (1999) · Disco · 121 BPM · 8A · 320 kbps MP3 · plus a very long comment that cannot fit";
            await Task.Delay(150);
            Assert.True(marquee.IsOverflowing);

            marquee.IsActive = true;                                                 // must not throw, and must be undone cleanly
            await Task.Delay(100);
            marquee.IsActive = false;
            marquee.Text = "Short again";
            await Task.Delay(100);
            Assert.False(marquee.IsOverflowing);
        }
        finally { window.Close(); }
    });
}
