using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure.Audio;

namespace MusicFinder.IntegrationTests;

/// <summary>The preview opens files read-only and shared, and plays on the real output at volume zero (silent). Nothing here writes to a music file.</summary>
public class PreviewPlayerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mf-preview-" + Guid.NewGuid().ToString("N"));

    public PreviewPlayerTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string Wav(string name, double seconds) =>
        AudioFixtures.WriteWav(Path.Combine(_dir, name), AudioFixtures.Sine(440, seconds, 44100), 44100, channels: 2);

    private static PreviewTrack TrackAt(string path) =>
        new(1, path, "Tone", "Test", null, null, null, null, null, null, null, Path.GetExtension(path), null);

    [Fact]
    public void AnOpenPreview_DoesNotBlockOthers_NotEvenARenameOrADelete()
    {
        var path = Wav("a.wav", 3);
        using var source = PreviewFileOpener.Open(path);

        using (var other = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete)) { }   // another program writing
        var moved = Path.Combine(_dir, "b.wav");
        File.Move(path, moved);                                                                                                    // an outside rename

        Assert.True(File.Exists(moved));
        Assert.True(source.Reader.TotalTime > TimeSpan.FromSeconds(2.5));                                                          // still readable from the handle
    }

    [Fact]
    public void Open_ReportsMissingAndUnsupportedFiles_AsReadableMessages()
    {
        var missing = Assert.Throws<PreviewOpenException>(() => PreviewFileOpener.Open(Path.Combine(_dir, "nope.mp3")));
        Assert.Equal("The file no longer exists.", missing.Message);

        var odd = Path.Combine(_dir, "x.xyz");
        File.WriteAllText(odd, "text");
        Assert.Contains(".xyz", Assert.Throws<PreviewOpenException>(() => PreviewFileOpener.Open(odd)).Message);

        var fake = Path.Combine(_dir, "broken.ogg");
        File.WriteAllText(fake, "not an ogg file");
        Assert.Contains("Ogg", Assert.Throws<PreviewOpenException>(() => PreviewFileOpener.Open(fake)).Message);
    }

    [Fact]
    public void Open_ReadsGreekPaths()
    {
        var path = AudioFixtures.WriteWav(Path.Combine(_dir, "Μουσική", "Χάρις Αλεξίου - Ξανά.wav"), AudioFixtures.Sine(440, 2, 44100), 44100);

        using var source = PreviewFileOpener.Open(path);

        Assert.True(source.Reader.TotalTime > TimeSpan.FromSeconds(1.5));
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 8000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    [Fact]
    public async Task Play_AtVolumeZero_MovesThroughPlayingToEnded_AndLetsGoOfTheFile()
    {
        var path = Wav("tone.wav", 1.5);
        using var player = new WasapiPreviewPlayer { Volume = 0f };

        await player.PlayAsync(TrackAt(path), default);
        await WaitForAsync(() => player.Status.State is not PreviewState.Loading);
        if (NoDevice(player)) return;

        Assert.Equal(PreviewState.Playing, player.Status.State);
        Assert.True(await WaitForAsync(() => player.Status.Position > TimeSpan.FromMilliseconds(200)));
        Assert.True(await WaitForAsync(() => player.Status.State == PreviewState.Ended, 10000));
        Assert.Equal(TimeSpan.Zero, player.Status.Position);
        Assert.True(await WaitForAsync(() => CanDelete(path)));                                                       // the handle is closed
    }

    [Fact]
    public async Task Pause_Resume_Seek_AndStop_Work_AndStopReleasesTheFile()
    {
        var path = Wav("long.wav", 20);
        using var player = new WasapiPreviewPlayer { Volume = 0f };
        await player.PlayAsync(TrackAt(path), default);
        await WaitForAsync(() => player.Status.State is not PreviewState.Loading);
        if (NoDevice(player)) return;

        player.Pause();
        Assert.Equal(PreviewState.Paused, player.Status.State);
        player.Seek(TimeSpan.FromSeconds(10));
        Assert.True(player.Status.Position >= TimeSpan.FromSeconds(9.9));
        player.Resume();
        Assert.Equal(PreviewState.Playing, player.Status.State);

        player.Stop();

        Assert.Equal(PreviewState.Ended, player.Status.State);
        Assert.True(CanDelete(path));                                                                                  // stopped means closed, immediately
    }

    [Fact]
    public async Task ReleaseIfPlaying_OnlyStopsThePlayingFile()
    {
        var path = Wav("one.wav", 20);
        var other = Wav("two.wav", 2);
        using var player = new WasapiPreviewPlayer { Volume = 0f };
        await player.PlayAsync(TrackAt(path), default);
        await WaitForAsync(() => player.Status.State is not PreviewState.Loading);
        if (NoDevice(player)) return;

        player.ReleaseIfPlaying(other);
        Assert.Equal(PreviewState.Playing, player.Status.State);

        player.ReleaseIfPlaying(path.ToUpperInvariant());
        Assert.Equal(PreviewState.Ended, player.Status.State);
        Assert.True(CanDelete(path));
    }

    [Fact]
    public async Task ANewRequest_ReplacesTheOldOne_AndAMissingChosenDevice_IsAnErrorNotTheDefault()
    {
        var path = Wav("a.wav", 20);
        using var player = new WasapiPreviewPlayer { Volume = 0f };
        player.DeviceId = "{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}";

        await player.PlayAsync(TrackAt(path), default);
        await WaitForAsync(() => player.Status.State is not PreviewState.Loading);

        Assert.Equal(PreviewState.Failed, player.Status.State);
        Assert.Contains("not available", player.Status.Error);
        Assert.True(CanDelete(path));

        player.DeviceId = null;                                                                                        // and the default still works afterwards
        await player.PlayAsync(TrackAt(path), default);
        await WaitForAsync(() => player.Status.State is not PreviewState.Loading);
        Assert.NotEqual(PreviewState.Loading, player.Status.State);
    }

    [Fact]
    public void Dispose_Twice_AndWithoutEverPlaying_DoesNothingBad()
    {
        var player = new WasapiPreviewPlayer();
        player.Dispose();
        player.Dispose();
        Assert.Equal(PreviewState.Idle, player.Status.State);
    }

    /// <summary>A machine without a sound device cannot play; any other failure is a real failure and fails the test.</summary>
    private static bool NoDevice(WasapiPreviewPlayer player) =>
        player.Status.State == PreviewState.Failed && player.Status.Error is { } e && e.Contains("no audio output");

    private static bool CanDelete(string path)
    {
        try { File.Delete(path); return !File.Exists(path); }
        catch (IOException) { return false; }
    }
}
