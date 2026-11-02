using MusicFinder.App.Services;
using MusicFinder.Domain.Contracts;

namespace MusicFinder.UiSmokeTests;

/// <summary>Closing the application: nothing is disposed twice with an error, even while work is still running.</summary>
public class ShutdownTests
{
    [Fact]
    public Task TheRealAudioDecoder_IsWiredIntoTheApplication_WithoutAnyTestReplacement() => Ui.RunAsync(async () =>
    {
        // Every other analysis test swaps the decoder for a fake; this one checks the container builds the real one (it once resolved itself: a circular dependency).
        var app = await UiTestApp.CreateAsync();

        Assert.IsType<MusicFinder.Infrastructure.Audio.AudioDecoderRouter>(app.Service<IAudioDecoder>());
        Assert.NotNull(app.Service<IAudioAnalysisService>());
        await app.DisposeAsync();
    });

    [Fact]
    public Task TheScanCoordinator_CanBeDisposedMoreThanOnce() => Ui.RunAsync(async () =>
    {
        var app = await UiTestApp.CreateAsync();
        var scans = app.Service<ScanCoordinator>();

        scans.Dispose();
        var second = Record.Exception(scans.Dispose);

        Assert.Null(second);
        await app.DisposeAsync();
    });

    [Fact]
    public Task TheExitSequence_ExplicitDisposalThenTheContainer_NeverThrows() => Ui.RunAsync(async () =>
    {
        // The same order as App.OnExit: end the session, dispose the coordinators by hand, then the container disposes them again.
        var app = await UiTestApp.CreateAsync();
        app.Window.Show();
        await app.Main.InitializeAsync();

        await app.Service<ISessionService>().ShutdownAsync();
        app.Service<ScanCoordinator>().Dispose();
        app.Service<SearchIndexCoordinator>().Dispose();
        var exception = await Record.ExceptionAsync(async () => await app.DisposeAsync());

        Assert.Null(exception);
    });

    [Fact]
    public Task ClosingWhileAScanIsRunning_DoesNotThrow() => Ui.RunAsync(async () =>
    {
        var app = await UiTestApp.CreateWithFilesAsync(null, ("a.mp3", "A", "X", "Disco"), ("b.mp3", "B", "X", "Disco"), ("c.mp3", "C", "X", "Disco"));
        app.Window.Show();
        await app.Main.InitializeAsync();
        var roots = await app.Service<ILibraryRootRepository>().GetAllAsync();
        var scans = app.Service<ScanCoordinator>();
        foreach (var r in roots) scans.Enqueue(r.Id);                       // a rescan is starting as the window is closed

        scans.Dispose();
        var exception = await Record.ExceptionAsync(async () => await app.DisposeAsync());

        Assert.Null(exception);
    });
}
