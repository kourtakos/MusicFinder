using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using MusicFinder.App.ViewModels;
using MusicFinder.App.Views;

namespace MusicFinder.UiSmokeTests;

public class ProbableDuplicatesUiTests
{
    private static readonly (string, string, string, string)[] Files =
    [
        ("a.mp3", "Frozen", "Madonna", "Pop"),
        ("b.mp3", "Frozen", "Madonna", "Pop"),
        ("c.mp3", "Frozen (Radio Edit)", "Madonna", "Pop"),
        ("d.mp3", "Vogue", "Madonna", "Pop"),
    ];

    private static async Task<(UiTestApp App, FakeDialogs Dialogs, ProbableDuplicatesViewModel Vm)> OpenAsync()
    {
        var dialogs = new FakeDialogs();
        var app = await UiTestApp.CreateWithFilesAsync(s => s.AddSingleton<MusicFinder.App.Services.IUserDialogs>(dialogs), Files);
        app.Window.Show();
        await app.Main.InitializeAsync();
        await Ui.WaitUntilAsync(() => app.Main.Library.Search.Results.Count == 4, what: "indexed rows");
        await app.Main.Health.FindProbableDuplicatesCommand.ExecuteAsync(null);
        var vm = Assert.IsType<ProbableDuplicatesViewModel>(dialogs.Probable);
        await vm.LoadAsync();
        return (app, dialogs, vm);
    }

    [Fact]
    public Task TheReview_ListsTheTwinsAndTheRadioEditApart_AndTheWindowHasNoBrokenBinding() => Ui.RunAsync(async () =>
    {
        var (app, _, vm) = await OpenAsync();
        await using var _ = app;

        Assert.Single(vm.SameRecording);
        Assert.Equal(2, vm.SameRecording[0].Members.Count);
        Assert.Single(vm.OtherVersions);
        Assert.Equal(3, vm.OtherVersions[0].Members.Count);
        Assert.Contains("1 groups of probable duplicates", vm.SummaryText);

        var window = new ProbableDuplicatesWindow(vm) { Owner = app.Window };
        window.Show();
        try
        {
            await Task.Delay(300);
            Assert.Empty(BindingAudit.FindBroken(window));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task AGroup_CanBeListedInTheLibrary_AndDismissed() => Ui.RunAsync(async () =>
    {
        var (app, _, vm) = await OpenAsync();
        await using var _ = app;
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        vm.ShowGroupInLibraryCommand.Execute(vm.SameRecording[0]);
        Assert.True(closed);
        Assert.StartsWith("artist:", app.Main.Library.Search.QueryText);
        await Ui.WaitUntilAsync(() => app.Main.Library.Search.Results.Count >= 2, what: "the song in the library");

        await vm.DismissCommand.ExecuteAsync(vm.SameRecording[0]);
        Assert.Empty(vm.SameRecording);
        await vm.LoadAsync();
        Assert.Empty(vm.SameRecording);                                   // stays hidden
    });

    [Fact]
    public Task TheReview_OffersNoWayToDeleteMoveOrRename() => Ui.RunAsync(async () =>
    {
        var (app, _, vm) = await OpenAsync();
        await using var _ = app;

        var names = typeof(ProbableDuplicatesViewModel).GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Select(m => m.Name).Where(n => !n.StartsWith("add_") && !n.StartsWith("remove_"))
            .Concat(typeof(SongMemberViewModel).GetMembers().Select(m => m.Name)).ToList();
        foreach (var banned in new[] { "Delete", "Remove", "Move", "Rename", "Trash", "Recycle", "Merge" })
            Assert.DoesNotContain(names, n => n.Contains(banned, StringComparison.OrdinalIgnoreCase));
        Assert.True(vm.RevealCommand.CanExecute(vm.SameRecording[0].Members[0]));
    });
}
