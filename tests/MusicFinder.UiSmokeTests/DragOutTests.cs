using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MusicFinder.App.Services.Dragging;
using MusicFinder.App.ViewModels;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Enums;

namespace MusicFinder.UiSmokeTests;

/// <summary>
/// The view-model side of dragging tracks out. The OS drag loop itself cannot be automated in a unit test;
/// it was exercised by hand against Media Player and Winamp (see docs/dj-session.md).
/// </summary>
public class DragOutTests
{
    private sealed class FakeDragSource : IFileDragSource
    {
        public string Effect { get; set; } = "Copy";
        public string? Target { get; set; } = "Traktor";
        public List<IReadOnlyList<string>> Drags { get; } = [];

        public DragOutcome StartDrag(IReadOnlyList<string> paths, object source)
        {
            Drags.Add(paths.ToList());
            var now = DateTime.UtcNow;
            return new DragOutcome(Effect, now.AddSeconds(-1), now, Target);
        }
    }

    private static readonly (string, string, string, string)[] Files =
    [
        ("a.mp3", "Alpha", "A", "Disco"),
        ("b.mp3", "Bravo", "B", "Funk"),
        ("c.mp3", "Charlie", "C", "Funk"),
    ];

    private sealed record Ctx(UiTestApp App, FakeDragSource Source, FakeTimeProvider Clock)
    {
        public SearchViewModel Search => App.Main.Library.Search;
        public ISessionService Session => App.Service<ISessionService>();
        public TrackRowViewModel Row(string title) => Search.Results.Single(r => r.Title == title);
        public void Select(params string[] titles) => Search.SetSelectedItems(titles.Select(Row).ToList());
    }

    private static async Task<Ctx> StartAsync()
    {
        var source = new FakeDragSource();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var app = await UiTestApp.CreateWithFilesAsync(s =>
        {
            s.AddSingleton<IFileDragSource>(source);
            s.AddSingleton<TimeProvider>(clock);
        }, Files);
        app.Window.Show();
        await app.Main.InitializeAsync();
        await Ui.WaitUntilAsync(() => app.Main.Library.Search.Results.Count == 3, what: "indexed rows");
        return new Ctx(app, source, clock);
    }

    [Fact]
    public Task Dragging_OffersTheRealPathsInSelectionOrder_AndRecordsAnExport() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.Select("Charlie", "Alpha");

        var outcome = c.Search.DragOut(new object());

        Assert.Equal("Copy", outcome!.Effect);
        var offered = Assert.Single(c.Source.Drags);
        Assert.Equal([Path.Combine(c.App.MusicDir, "c.mp3"), Path.Combine(c.App.MusicDir, "a.mp3")], offered);

        await Ui.WaitUntilAsync(() => c.Session.Active is not null, what: "session started by the drag");
        var entries = await c.Session.GetEntriesAsync(c.Session.Active!.Id, default);
        Assert.Equal(["Alpha", "Charlie"], entries.Select(e => e.Title).Order());
        Assert.All(entries, e => Assert.Equal((SessionPlayStatus.Exported, "Copy", "Traktor"), (e.Status, e.Effect, e.Target)));
        Assert.All(c.Search.Results, r => Assert.Equal(0, r.Plays));                          // an export is not a play
    });

    [Fact]
    public Task ACancelledDrag_RecordsNothing() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.Source.Effect = "None";
        c.Select("Alpha");

        c.Search.DragOut(new object());
        await Task.Delay(200);

        Assert.Null(c.Session.Active);
    });

    [Fact]
    public Task MissingFiles_AreLeftOut_AndTheUserIsTold() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        File.Delete(Path.Combine(c.App.MusicDir, "b.mp3"));
        c.Select("Alpha", "Bravo");

        c.Search.DragOut(new object());

        Assert.Equal([Path.Combine(c.App.MusicDir, "a.mp3")], Assert.Single(c.Source.Drags));
        Assert.Contains("1 missing file(s) were left out", c.Search.Warning);
    });

    [Fact]
    public Task WhenEverythingIsMissing_NothingIsDragged() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        File.Delete(Path.Combine(c.App.MusicDir, "a.mp3"));
        c.Select("Alpha");

        var outcome = c.Search.DragOut(new object());

        Assert.Null(outcome);
        Assert.Empty(c.Source.Drags);
        Assert.Contains("not on disk", c.Search.Warning);
    });

    [Fact]
    public Task DraggingWithNothingSelected_DoesNothing() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;

        Assert.Null(c.Search.DragOut(new object()));
        Assert.Empty(c.Source.Drags);
    });
}
