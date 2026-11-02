using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Security.Cryptography;
using MusicFinder.App.ViewModels;
using MusicFinder.App.Views;
using MusicFinder.Domain.Models;

namespace MusicFinder.UiSmokeTests;

/// <summary>The duplicate files review on real generated files: it finds, shows and lists, and offers no way to remove anything.</summary>
public class DuplicatesUiTests
{
    // a and b carry identical tags on identical audio, so they are byte for byte the same file; c differs.
    private static readonly (string, string, string, string)[] Files =
    [
        ("a.mp3", "Same", "Twin", "Disco"),
        ("b.mp3", "Same", "Twin", "Disco"),
        ("c.mp3", "Other", "Solo", "Funk"),
    ];

    private sealed record Ctx(UiTestApp App, FakeDialogs Dialogs);

    private static async Task<Ctx> StartAsync()
    {
        var dialogs = new FakeDialogs();
        var app = await UiTestApp.CreateWithFilesAsync(s => s.AddSingleton<MusicFinder.App.Services.IUserDialogs>(dialogs), Files);
        app.Window.Show();
        await app.Main.InitializeAsync();
        await Ui.WaitUntilAsync(() => app.Main.Library.Search.Results.Count == 3, what: "indexed rows");
        return new Ctx(app, dialogs);
    }

    private static string Snapshot(string dir) => string.Join("|", Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
        .OrderBy(f => f).Select(f => f + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))));

    [Fact]
    public Task TheScan_FindsTheTwins_AndTheWindowShowsThemWithoutAnyBrokenBinding() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var before = Snapshot(c.App.MusicDir);

        await c.App.Main.Health.FindDuplicatesCommand.ExecuteAsync(null);
        var vm = Assert.IsType<DuplicatesViewModel>(c.Dialogs.Duplicates);
        await vm.LoadAsync();
        Assert.False(vm.HasGroups);                                   // nothing is hashed until the user asks

        await vm.ScanCommand.ExecuteAsync(null);

        var group = Assert.Single(vm.Groups);
        Assert.Equal(["a.mp3", "b.mp3"], group.Members.Select(m => m.FileName).OrderBy(n => n));
        Assert.Single(group.Members, m => m.IsSuggested);
        Assert.Contains("2 identical files", group.DetailText);
        Assert.Contains("Done.", vm.StatusText);
        Assert.Equal(before, Snapshot(c.App.MusicDir));               // not one byte of any music file changed

        var window = new DuplicatesWindow(vm) { Owner = c.App.Window };
        window.Show();
        try
        {
            await Task.Delay(300);
            Assert.Empty(BindingAudit.FindBroken(window));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task TheHealthCard_CountsTheTwins_AndOpensTheLibraryOnThem() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        await c.App.Main.Health.FindDuplicatesCommand.ExecuteAsync(null);
        var vm = c.Dialogs.Duplicates!;
        await vm.ScanCommand.ExecuteAsync(null);

        c.App.Main.SelectedTab = AppTab.Health;
        await c.App.Main.Health.ReloadAsync();
        var card = c.App.Main.Health.Groups.SelectMany(g => g.Cards).Single(x => x.Kind == HealthIssueKind.ExactDuplicates);
        Assert.Equal(2, card.Count);

        card.ShowCommand.Execute(null);

        Assert.Equal(AppTab.Library, c.App.Main.SelectedTab);
        Assert.Equal("dup:exact", c.App.Main.Library.Search.QueryText);
        await Ui.WaitUntilAsync(() => c.App.Main.Library.Search.Results.Count == 2, what: "the twins");
        Assert.Equal(["Same", "Same"], c.App.Main.Library.Search.Results.Select(r => r.Title));
    });

    [Fact]
    public Task AGroup_CanBeListedInTheLibrary_AndDismissed() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        await c.App.Main.Health.FindDuplicatesCommand.ExecuteAsync(null);
        var vm = c.Dialogs.Duplicates!;
        await vm.ScanCommand.ExecuteAsync(null);
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        vm.ShowGroupInLibraryCommand.Execute(vm.Groups[0]);
        Assert.True(closed);
        Assert.StartsWith("dup:", c.App.Main.Library.Search.QueryText);
        await Ui.WaitUntilAsync(() => c.App.Main.Library.Search.Results.Count == 2, what: "the group");

        await vm.DismissCommand.ExecuteAsync(vm.Groups[0]);
        Assert.Empty(vm.Groups);
        Assert.False(vm.HasGroups);
        await vm.ScanCommand.ExecuteAsync(null);                       // a later scan does not bring it back
        Assert.Empty(vm.Groups);
    });

    [Fact]
    public Task OpenInExplorer_RevealsTheFile_AndItIsTheOnlyActionOnAFile() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        await c.App.Main.Health.FindDuplicatesCommand.ExecuteAsync(null);
        var vm = c.Dialogs.Duplicates!;
        await vm.ScanCommand.ExecuteAsync(null);

        // The review must not offer a way to remove, move or rename anything: no such member exists at all.
        var names = typeof(DuplicatesViewModel).GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Select(m => m.Name).Where(n => !n.StartsWith("add_") && !n.StartsWith("remove_")).Concat(typeof(DuplicateMemberViewModel).GetMembers().Select(m => m.Name)).ToList();
        foreach (var banned in new[] { "Delete", "Remove", "Move", "Rename", "Trash", "Recycle" })
            Assert.DoesNotContain(names, n => n.Contains(banned, StringComparison.OrdinalIgnoreCase) && !n.Contains("ShowGroup"));
        Assert.True(vm.RevealCommand.CanExecute(vm.Groups[0].Members[0]));
    });
}
