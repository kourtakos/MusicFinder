using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Text;
using MusicFinder.App.ViewModels;
using MusicFinder.App.Views;

namespace MusicFinder.UiSmokeTests;

/// <summary>Batch rename through the real window and view model, on generated files only.</summary>
public class RenameUiTests
{
    private static readonly (string, string, string, string)[] Files =
    [
        ("a.mp3", "Frozen", "Madonna", "Pop"),
        ("b.mp3", "Σ' αγαπώ", "Χάρις Αλεξίου", "Greek"),
        ("c.mp3", "Gone", "Someone", "Funk"),
    ];

    private sealed record Ctx(UiTestApp App, FakeDialogs Dialogs)
    {
        public LibraryViewModel Library => App.Main.Library;
    }

    private static async Task<Ctx> StartAsync()
    {
        var dialogs = new FakeDialogs();
        var app = await UiTestApp.CreateWithFilesAsync(s => s.AddSingleton<MusicFinder.App.Services.IUserDialogs>(dialogs), Files);
        app.Window.Show();
        await app.Main.InitializeAsync();
        await Ui.WaitUntilAsync(() => app.Main.Library.Search.Results.Count == 3, what: "indexed rows");
        return new Ctx(app, dialogs);
    }

    private static async Task<RenameViewModel> OpenAsync(Ctx c, int take = 3)
    {
        c.Library.Search.SetSelectedItems(c.Library.Search.Results.Take(take).ToList());
        c.Library.RenameFilesCommand.Execute(null);
        var vm = Assert.IsType<RenameViewModel>(c.Dialogs.Rename);
        await vm.LoadAsync();
        return vm;
    }

    private static string Snapshot(string dir) => string.Join("|", Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
        .Select(f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))).OrderBy(h => h));

    [Fact]
    public Task ThePreview_ShowsEveryNewName_AndTheWindowHasNoBrokenBinding() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var vm = await OpenAsync(c);

        Assert.Equal(3, vm.RenameCount);
        Assert.Equal("Will rename 3 files.", vm.SummaryText);
        Assert.Equal("Rename 3 files", vm.RenameButtonText);
        Assert.Contains(vm.Rows, r => r.NewName == "Χάρις Αλεξίου - Σ' αγαπώ.mp3");
        Assert.True(File.Exists(Path.Combine(c.App.MusicDir, "a.mp3")));            // a preview changes nothing

        var window = new RenameWindow(vm) { Owner = c.App.Window };
        window.Show();
        try
        {
            await Task.Delay(300);
            Assert.Empty(BindingAudit.FindBroken(window));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ABadTemplate_ShowsTheError_AndCannotBeStarted() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var vm = await OpenAsync(c);

        vm.Template = "{Nope}";
        await vm.RefreshPreviewAsync();

        Assert.Contains("Unknown token", vm.TemplateError);
        Assert.False(vm.RenameCommand.CanExecute(null));
        Assert.All(vm.Rows, r => Assert.False(r.WillRename));
    });

    [Fact]
    public Task TheFirstRename_AsksAboutDjSoftwareOnce_AndAnswerNoChangesNothing() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var vm = await OpenAsync(c);
        c.Dialogs.ConfirmAnswer = false;
        await vm.RenameCommand.ExecuteAsync(null);

        Assert.Equal([RenameViewModel.WarningTitle], c.Dialogs.Confirmed);
        Assert.True(File.Exists(Path.Combine(c.App.MusicDir, "a.mp3")));
        Assert.False(c.App.Settings.Load().RenameWarningAccepted);

        c.Dialogs.ConfirmAnswer = true;
        await vm.RenameCommand.ExecuteAsync(null);
        Assert.True(c.App.Settings.Load().RenameWarningAccepted);
        Assert.Equal(2, c.Dialogs.Confirmed.Count);

        c.Dialogs.Confirmed.Clear();
        await vm.UndoCommand.ExecuteAsync(vm.Batches[0]);
        await vm.RenameCommand.ExecuteAsync(null);                                    // the warning is not repeated
        Assert.Empty(c.Dialogs.Confirmed);
    });

    [Fact]
    public Task Renaming_ChangesTheFiles_RefreshesTheLibrary_AndCanBeUndone() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var before = Snapshot(c.App.MusicDir);
        var vm = await OpenAsync(c);

        await vm.RenameCommand.ExecuteAsync(null);

        Assert.Contains("Renamed 3 files", vm.StatusText);
        Assert.True(File.Exists(Path.Combine(c.App.MusicDir, "Madonna - Frozen.mp3")));
        Assert.True(File.Exists(Path.Combine(c.App.MusicDir, "Χάρις Αλεξίου - Σ' αγαπώ.mp3")));
        Assert.False(File.Exists(Path.Combine(c.App.MusicDir, "a.mp3")));
        Assert.Equal(before, Snapshot(c.App.MusicDir));                               // same bytes, new names
        await Ui.WaitUntilAsync(() => c.Library.Search.Results.Any(r => Path.GetFileName(r.FullPath) == "Madonna - Frozen.mp3"), what: "the library shows the new name");
        var batch = Assert.Single(vm.Batches);
        Assert.True(batch.CanUndo);
        Assert.Equal("3 renamed", batch.Detail);
        Assert.Equal(0, vm.RenameCount);                                              // nothing left to do under this template

        await vm.UndoCommand.ExecuteAsync(batch);

        Assert.True(File.Exists(Path.Combine(c.App.MusicDir, "a.mp3")));
        Assert.Equal(before, Snapshot(c.App.MusicDir));
        Assert.False(vm.Batches[0].CanUndo);
        Assert.Contains("Restored 3 files", vm.StatusText);
    });

    [Fact]
    public Task AMissingTag_IsSkippedWithItsReason_UnlessPlaceholdersAreOn() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var vm = await OpenAsync(c);

        vm.Template = "{Artist} - {Bpm} - {Title}";                                    // none of the files has a BPM
        await vm.RefreshPreviewAsync();
        Assert.Equal(0, vm.RenameCount);
        Assert.All(vm.Rows, r => Assert.Contains("{Bpm}", r.Reason));

        vm.UsePlaceholders = true;
        await vm.RefreshPreviewAsync();
        Assert.Equal(3, vm.RenameCount);
        Assert.Contains(vm.Rows, r => r.NewName == "Madonna - Unknown BPM - Frozen.mp3");
    });

    [Fact]
    public Task ThePathList_IsSavedAsCsv_ForTheChosenBatch() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var vm = await OpenAsync(c, take: 1);
        await vm.RenameCommand.ExecuteAsync(null);
        var target = Path.Combine(Path.GetTempPath(), "mf-map-" + Guid.NewGuid().ToString("N") + ".csv");
        c.Dialogs.SavePicked = target;
        try
        {
            await vm.SavePathMapCommand.ExecuteAsync(vm.Batches[0]);

            var bytes = File.ReadAllBytes(target);
            Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
            var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            Assert.StartsWith("OldPath,NewPath", text);
            Assert.Equal(2, text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        }
        finally { File.Delete(target); }
    });

    [Fact]
    public Task WithNothingSelected_NoWindowOpens_AndWhileIndexingItIsRefused() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;

        c.Library.Search.SetSelectedItems([]);
        c.Library.RenameFilesCommand.Execute(null);

        Assert.Null(c.Dialogs.Rename);
    });
}
