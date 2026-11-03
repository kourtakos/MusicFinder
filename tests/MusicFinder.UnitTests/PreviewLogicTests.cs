using MusicFinder.Application.Preview;
using MusicFinder.Domain.Models;

namespace MusicFinder.UnitTests;

public class PreviewLogicTests
{
    private static PreviewTrack Track(string title = "Song", string? artist = "Artist", string? album = "Album", int? year = 1999,
        string? genre = "Disco", double? bpm = 121.4, string? key = "8A", long? ms = 225_000, int? kbps = 320, string ext = ".mp3") =>
        new(1, @"C:\m\a.mp3", title, artist, album, year, genre, bpm, key, ms, kbps, ext, null);

    // ----- time -----

    [Theory]
    [InlineData(0, 225, "00:00 / 03:45")]
    [InlineData(83, 225, "01:23 / 03:45")]
    [InlineData(3599, 3601, "59:59 / 1:00:01")]
    [InlineData(3725, 7200, "1:02:05 / 2:00:00")]
    public void Time_IsMinutesAndSeconds_AndHoursOnlyWhenNeeded(int elapsed, int total, string expected) =>
        Assert.Equal(expected, PreviewTime.Format(TimeSpan.FromSeconds(elapsed), TimeSpan.FromSeconds(total)));

    [Fact]
    public void Time_WithoutAKnownLength_ShowsDashes_AndNeverNegative()
    {
        Assert.Equal("00:05 / --:--", PreviewTime.Format(TimeSpan.FromSeconds(5), null));
        Assert.Equal("00:00 / --:--", PreviewTime.Format(TimeSpan.FromSeconds(-3), TimeSpan.Zero));
    }

    [Fact]
    public void Time_IgnoresTheCurrentCulture()
    {
        var old = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");
            Assert.Equal("01:05 / 02:00", PreviewTime.Format(TimeSpan.FromSeconds(65), TimeSpan.FromSeconds(120)));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = old; }
    }

    // ----- caption -----

    [Fact]
    public void Caption_ListsWhatIsKnown_InOrder()
    {
        Assert.Equal("Artist · Album (1999) · Disco · 121 BPM · 8A · 320 kbps MP3", PreviewCaption.Build(Track()));
    }

    [Fact]
    public void Caption_SkipsMissingFields_WithoutLeavingGaps()
    {
        var caption = PreviewCaption.Build(Track(artist: null, album: null, year: null, genre: "", bpm: null, key: " ", kbps: null));

        Assert.Equal("MP3", caption);
    }

    [Fact]
    public void Caption_ShowsAYearWithoutAnAlbum_AndNeverInventsValues()
    {
        Assert.Equal("Artist · 1999 · MP3", PreviewCaption.Build(Track(album: null, genre: null, bpm: null, key: null, kbps: null)));
    }

    // ----- state machine -----

    [Fact]
    public void ThePlayRoute_GoesLoadingPlayingEnded_AndTheTrackStaysLoaded()
    {
        var s = PreviewStateMachine.Requested(PreviewStatus.Idle, Track());
        Assert.Equal(PreviewState.Loading, s.State);
        Assert.Equal("Song", s.Track!.Title);

        s = PreviewStateMachine.Started(s, TimeSpan.FromSeconds(200));
        Assert.Equal(PreviewState.Playing, s.State);
        Assert.Equal(TimeSpan.FromSeconds(200), s.Duration);

        s = PreviewStateMachine.Ended(s);
        Assert.Equal(PreviewState.Ended, s.State);
        Assert.Equal(TimeSpan.Zero, s.Position);
        Assert.NotNull(s.Track);
    }

    [Fact]
    public void PauseAndResume_OnlyApplyInTheRightState()
    {
        var playing = PreviewStateMachine.Started(PreviewStateMachine.Requested(PreviewStatus.Idle, Track()), TimeSpan.FromMinutes(3));

        var paused = PreviewStateMachine.Paused(playing);
        Assert.Equal(PreviewState.Paused, paused.State);
        Assert.Same(PreviewStatus.Idle, PreviewStateMachine.Paused(PreviewStatus.Idle));          // nothing to pause
        Assert.Equal(PreviewState.Playing, PreviewStateMachine.Resumed(paused).State);
        Assert.Equal(PreviewState.Ended, PreviewStateMachine.Resumed(PreviewStateMachine.Ended(playing)).State); // a finished track needs Play, not Resume
    }

    [Fact]
    public void ANewRequest_WhileSomethingPlays_ReplacesIt_AndClearsTheOldError()
    {
        var failed = PreviewStateMachine.Failed(PreviewStateMachine.Requested(PreviewStatus.Idle, Track("Old")), "The file no longer exists.");
        Assert.Equal(PreviewState.Failed, failed.State);
        Assert.Equal("The file no longer exists.", failed.Error);

        var next = PreviewStateMachine.Requested(failed, Track("New"));

        Assert.Equal(PreviewState.Loading, next.State);
        Assert.Null(next.Error);
        Assert.Equal("New", next.Track!.Title);
        Assert.Equal(TimeSpan.Zero, next.Position);
    }

    [Fact]
    public void Seek_IsClampedToTheTrack()
    {
        var playing = PreviewStateMachine.Started(PreviewStateMachine.Requested(PreviewStatus.Idle, Track()), TimeSpan.FromSeconds(100));

        Assert.Equal(TimeSpan.FromSeconds(40), PreviewStateMachine.Moved(playing, TimeSpan.FromSeconds(40)).Position);
        Assert.Equal(TimeSpan.Zero, PreviewStateMachine.Moved(playing, TimeSpan.FromSeconds(-5)).Position);
        Assert.Equal(TimeSpan.FromSeconds(100), PreviewStateMachine.Moved(playing, TimeSpan.FromSeconds(500)).Position);
    }

    [Fact]
    public void Stop_RewindsButKeepsTheTrack_AndIsHarmlessWhenIdle()
    {
        var playing = PreviewStateMachine.Moved(PreviewStateMachine.Started(PreviewStateMachine.Requested(PreviewStatus.Idle, Track()), TimeSpan.FromSeconds(100)), TimeSpan.FromSeconds(30));

        var stopped = PreviewStateMachine.Stopped(playing);

        Assert.Equal(PreviewState.Ended, stopped.State);
        Assert.Equal(TimeSpan.Zero, stopped.Position);
        Assert.NotNull(stopped.Track);
        Assert.Same(PreviewStatus.Idle, PreviewStateMachine.Stopped(PreviewStatus.Idle));
    }
}
