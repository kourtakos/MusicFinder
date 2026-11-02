using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using MusicFinder.App.Services;
using MusicFinder.App.ViewModels;
using MusicFinder.App.Views;
using MusicFinder.Domain.Models;

namespace MusicFinder.UiSmokeTests;

/// <summary>A dialog service that records what would have been shown instead of opening modal windows.</summary>
internal sealed class FakeDialogs : IUserDialogs
{
    public TagEditorViewModel? Editor { get; private set; }
    public List<string> Infos { get; } = [];
    public string? PickFolder() => null;
    public List<string> Confirmed { get; } = [];
    public bool ConfirmAnswer { get; set; } = true;
    public bool Confirm(string title, string message) { Confirmed.Add(title); return ConfirmAnswer; }
    public void ShowError(string title, string message) { }
    public void ShowInfo(string title, string message) => Infos.Add(message);
    public void RevealInExplorer(string path) { }
    public void CopyToClipboard(string text) { }
    public void ShowTagEditor(TagEditorViewModel viewModel) => Editor = viewModel;
    public IdentifyViewModel? Identify { get; private set; }
    public IdentificationConsentViewModel? Consent { get; private set; }
    /// <summary>What the user answers when the consent dialog opens; the view model saves its own settings when accepted.</summary>
    public Action<IdentificationConsentViewModel>? OnConsent { get; set; }
    public string? FilePicked { get; set; }
    public void ShowIdentificationConsent(IdentificationConsentViewModel viewModel) { Consent = viewModel; OnConsent?.Invoke(viewModel); }
    public void ShowIdentify(IdentifyViewModel viewModel) => Identify = viewModel;
    public AnalyzeViewModel? Analyze { get; private set; }
    public void ShowAnalyze(AnalyzeViewModel viewModel) => Analyze = viewModel;
    public DuplicatesViewModel? Duplicates { get; private set; }
    public void ShowDuplicates(DuplicatesViewModel viewModel) => Duplicates = viewModel;
    public ProbableDuplicatesViewModel? Probable { get; private set; }
    public void ShowProbableDuplicates(ProbableDuplicatesViewModel viewModel) => Probable = viewModel;
    public RenameViewModel? Rename { get; private set; }
    public void ShowRename(RenameViewModel viewModel) => Rename = viewModel;
    public string? SavePicked { get; set; }
    public string? PickSaveFile(string title, string filter, string defaultName) => SavePicked;
    public string? PickFile(string title, string filter) => FilePicked;
}

public class TagEditorTests
{
    private static readonly (string, string, string, string)[] Files =
    [
        ("a.mp3", "Alpha", "Artist One", "Disco"),
        ("b.mp3", "Bravo", "Artist One", "Funk"),
        ("c.mp3", "Charlie", "Artist Two", "Disco"),
    ];

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed record Ctx(UiTestApp App, FakeDialogs Dialogs)
    {
        public SearchViewModel Search => App.Main.Library.Search;
    }

    private static async Task<Ctx> StartAsync()
    {
        var dialogs = new FakeDialogs();
        var app = await UiTestApp.CreateWithFilesAsync(s => s.AddSingleton<IUserDialogs>(dialogs), Files);
        app.Window.Show();
        await app.Main.InitializeAsync();
        await Ui.WaitUntilAsync(() => app.Main.Library.Search.Results.Count == 3, what: "indexed rows");
        return new Ctx(app, dialogs);
    }

    private static async Task<TagEditorViewModel> OpenEditorAsync(Ctx c, params string[] titles)
    {
        var rows = c.Search.Results.Where(r => titles.Contains(r.Title)).ToList();
        c.Search.SetSelectedItems(rows);
        await c.App.Main.Library.EditTagsCommand.ExecuteAsync(null);
        Assert.NotNull(c.Dialogs.Editor);
        return c.Dialogs.Editor!;
    }

    private static TagFieldEditor Field(TagEditorViewModel vm, TagField f) => vm.Fields.Single(x => x.Field == f);

    [Fact]
    public Task EditTags_OpensTheEditor_WithCurrentValues_AndMarksMixedFields() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;

        var vm = await OpenEditorAsync(c, "Alpha", "Bravo", "Charlie");

        Assert.Equal(9, vm.Fields.Count);
        Assert.Equal("Edit tags of 3 tracks", vm.Heading);

        var title = Field(vm, TagField.Title);
        Assert.True(title.IsMixed);                              // three different titles
        Assert.Equal("", title.Text);                            // no arbitrary value is pre-filled
        Assert.Equal("3 different values", title.MixedText);
        Assert.Contains("multiple values", title.Placeholder);

        var album = Field(vm, TagField.Album);
        Assert.False(album.IsMixed);                             // all share the album
        Assert.Equal("Original Album", album.Text);

        Assert.DoesNotContain(vm.Fields, f => f.Apply);          // nothing is selected for change yet
    });

    [Fact]
    public Task SingleTrack_ShowsItsValuesAndPath() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;

        var vm = await OpenEditorAsync(c, "Alpha");

        Assert.Equal("Edit tags", vm.Heading);
        Assert.EndsWith("a.mp3", vm.SubHeading);
        Assert.Equal("Alpha", Field(vm, TagField.Title).Text);
        Assert.Equal("Artist One", Field(vm, TagField.Artist).Text);
        Assert.False(Field(vm, TagField.Title).IsMixed);
    });

    [Fact]
    public Task EditTags_IsRefused_WhileTheLibraryIsBeingIndexed() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.Search.SetSelectedItems([c.Search.Results[0]]);

        // Simulate an active scan on the coordinator.
        var scans = c.App.Service<ScanCoordinator>();
        typeof(ScanCoordinator).GetProperty(nameof(ScanCoordinator.IsScanning))!.SetValue(scans, true);

        await c.App.Main.Library.EditTagsCommand.ExecuteAsync(null);

        Assert.Null(c.Dialogs.Editor);
        Assert.Contains(c.Dialogs.Infos, m => m.Contains("indexing"));
    });

    [Fact]
    public Task EditTags_DoesNothing_WithoutASelection() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.Search.SetSelectedItems([]);

        await c.App.Main.Library.EditTagsCommand.ExecuteAsync(null);

        Assert.Null(c.Dialogs.Editor);
    });

    [Fact]
    public Task Typing_SelectsTheFieldForChange_AndInvalidValuesAreFlaggedLive() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var vm = await OpenEditorAsync(c, "Alpha", "Bravo");
        var year = Field(vm, TagField.Year);

        year.Text = "19";
        Assert.True(year.Apply);                    // typing is intent
        Assert.NotNull(year.Error);

        year.Text = "1985";
        Assert.Null(year.Error);

        year.Text = "1800";
        Assert.NotNull(year.Error);
        year.Apply = false;                          // unticking drops the change, so its error disappears too
        Assert.Null(year.Error);
        year.Text = "1801";
        Assert.True(year.Apply);                     // typing again re-selects it...
        Assert.NotNull(year.Error);                  // ...and the bad value is flagged again
    });

    [Fact]
    public Task Preview_IsBlocked_ForNothingSelected_InvalidValues_AndNoOpEdits() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var vm = await OpenEditorAsync(c, "Alpha", "Bravo");

        await vm.PreviewCommand.ExecuteAsync(null);
        Assert.Equal(TagEditorStep.Edit, vm.Step);
        Assert.Contains("Nothing to change", vm.Message);

        Field(vm, TagField.Year).Text = "1700";
        await vm.PreviewCommand.ExecuteAsync(null);
        Assert.Equal(TagEditorStep.Edit, vm.Step);
        Assert.Contains("Fix the highlighted", vm.Message);

        Field(vm, TagField.Year).Apply = false;
        Field(vm, TagField.Album).Apply = true; // ticked, but the text is already what both files have ("Original Album")
        Assert.Equal("Original Album", Field(vm, TagField.Album).Text);
        await vm.PreviewCommand.ExecuteAsync(null);
        Assert.Equal(TagEditorStep.Edit, vm.Step);
        Assert.Contains("already has these values", vm.Message);
    });

    [Fact]
    public Task Preview_ShowsTheDiff_ThenBackKeepsWhatWasTyped() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var vm = await OpenEditorAsync(c, "Alpha", "Bravo", "Charlie");
        Field(vm, TagField.Album).Text = "Greatest Hits";
        Field(vm, TagField.Genre).Text = "Disco; Funk";

        await vm.PreviewCommand.ExecuteAsync(null);

        Assert.Equal(TagEditorStep.Preview, vm.Step);
        Assert.Equal("Write 3 files", vm.WriteButtonText);
        Assert.Contains(vm.PreviewRows, r => r is { FileName: "a.mp3", Field: TagField.Album, Before: "Original Album", After: "Greatest Hits" });
        Assert.Contains(vm.PreviewRows, r => r is { FileName: "b.mp3", Field: TagField.Genre, Before: "Funk", After: "Disco; Funk" });
        Assert.Contains("Nothing is written", vm.PreviewSummary);

        vm.BackCommand.Execute(null);
        Assert.Equal(TagEditorStep.Edit, vm.Step);
        Assert.Equal("Greatest Hits", Field(vm, TagField.Album).Text); // nothing was lost by going back
        Assert.True(Field(vm, TagField.Album).Apply);

        // A preview never writes: files are still the originals.
        foreach (var name in new[] { "a.mp3", "b.mp3", "c.mp3" })
        {
            using var f = TagLib.File.Create(Path.Combine(c.App.MusicDir, name));
            Assert.Equal("Original Album", f.Tag.Album);
        }
    });

    [Fact]
    public Task Write_ChangesTheFiles_AndTheGridRowsUpdateInPlace() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var rowAlpha = c.Search.Results.Single(r => r.Title == "Alpha");
        c.Search.SetSelectedItems([rowAlpha]);
        var vm = await OpenEditorAsync(c, "Alpha");
        Field(vm, TagField.Title).Text = "Ξανά στη ζωή μου";
        Field(vm, TagField.Genre).Text = "Nu-Disco";
        await vm.PreviewCommand.ExecuteAsync(null);

        await vm.WriteCommand.ExecuteAsync(null);

        Assert.Equal(TagEditorStep.Done, vm.Step);
        Assert.True(vm.AnyWritten);
        Assert.Contains("1 file(s) written", vm.ResultSummary);
        Assert.Empty(vm.Failures);

        using (var f = TagLib.File.Create(Path.Combine(c.App.MusicDir, "a.mp3")))
        {
            Assert.Equal("Ξανά στη ζωή μου", f.Tag.Title);
            Assert.Equal(["Nu-Disco"], f.Tag.Genres);
        }

        // The same row object shows the new values (selection and scroll are not disturbed).
        await Ui.WaitUntilAsync(() => rowAlpha.Title == "Ξανά στη ζωή μου", what: "row refreshed");
        Assert.Same(rowAlpha, c.Search.Results.Single(r => r.Id == rowAlpha.Id));
        Assert.Equal(["Nu Disco"], rowAlpha.Genres);                      // mapped through the taxonomy
        await Ui.WaitUntilAsync(() => c.Search.GenreOptions.Any(o => o.Label == "Nu Disco"), what: "facets refreshed");
    });

    [Fact]
    public Task BulkWrite_ChangesOnlyTheSelectedFields_OnEveryTrack() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var vm = await OpenEditorAsync(c, "Alpha", "Bravo", "Charlie");
        Field(vm, TagField.Artist).Text = "Various Artists";
        Field(vm, TagField.Year).Text = "2001";
        await vm.PreviewCommand.ExecuteAsync(null);
        await vm.WriteCommand.ExecuteAsync(null);

        Assert.Contains("3 file(s) written", vm.ResultSummary);
        var titles = new List<string>();
        foreach (var name in new[] { "a.mp3", "b.mp3", "c.mp3" })
        {
            using var f = TagLib.File.Create(Path.Combine(c.App.MusicDir, name));
            Assert.Equal("Various Artists", f.Tag.FirstPerformer);
            Assert.Equal(2001u, f.Tag.Year);
            Assert.Equal("Original Album", f.Tag.Album);   // not selected: untouched
            titles.Add(f.Tag.Title);
        }
        Assert.Equal(["Alpha", "Bravo", "Charlie"], titles); // mixed titles stayed as they were
    });

    [Fact]
    public Task ABlankTickedField_ClearsIt_Explicitly() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var vm = await OpenEditorAsync(c, "Alpha");
        var album = Field(vm, TagField.Album);
        album.Text = "";
        Assert.True(album.Apply);
        Assert.Equal("(will be cleared)", album.Placeholder);

        await vm.PreviewCommand.ExecuteAsync(null);
        Assert.Contains(vm.PreviewRows, r => r is { Field: TagField.Album, After: null });
        await vm.WriteCommand.ExecuteAsync(null);

        using var f = TagLib.File.Create(Path.Combine(c.App.MusicDir, "a.mp3"));
        Assert.True(string.IsNullOrEmpty(f.Tag.Album));
        Assert.Equal("Alpha", f.Tag.Title);
    });

    [Fact]
    public Task APartialFailure_IsReportedPerFile_AndOthersStillSucceed() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var locked = Path.Combine(c.App.MusicDir, "b.mp3");
        File.SetAttributes(locked, FileAttributes.ReadOnly);
        var lockedHash = Hash(locked);
        try
        {
            var vm = await OpenEditorAsync(c, "Alpha", "Bravo", "Charlie");
            Field(vm, TagField.Album).Text = "New Album";
            await vm.PreviewCommand.ExecuteAsync(null);
            await vm.WriteCommand.ExecuteAsync(null);

            Assert.Equal(TagEditorStep.Done, vm.Step);
            Assert.Contains("2 file(s) written", vm.ResultSummary);
            Assert.Contains("1 failed", vm.ResultSummary);
            Assert.Contains("left untouched", vm.ResultSummary);
            var failure = Assert.Single(vm.Failures);
            Assert.EndsWith("b.mp3", failure.Path);
            Assert.Contains("read-only", failure.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(vm.HasFailures);
            Assert.Equal(lockedHash, Hash(locked));
        }
        finally
        {
            File.SetAttributes(locked, FileAttributes.Normal);
        }
    });

    [Fact]
    public Task BackupOption_IsRemembered_AndWritesBackupsInTheDataFolder() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var original = Hash(Path.Combine(c.App.MusicDir, "a.mp3"));
        var vm = await OpenEditorAsync(c, "Alpha");
        Assert.False(vm.KeepBackup);

        vm.KeepBackup = true;
        Assert.True(c.App.Settings.Load().KeepTagBackups);   // remembered for next time

        Field(vm, TagField.Album).Text = "Backed up";
        await vm.PreviewCommand.ExecuteAsync(null);
        await vm.WriteCommand.ExecuteAsync(null);

        var backups = Directory.GetFiles(Path.Combine(c.App.MusicDir, "..", "Data", "TagBackups"), "*a.mp3", SearchOption.AllDirectories);
        var backup = Assert.Single(backups);
        Assert.Equal(original, Hash(backup));                           // the pre-edit bytes
        Assert.DoesNotContain(Directory.GetFiles(c.App.MusicDir), f => f.Contains("backup", StringComparison.OrdinalIgnoreCase));
    });

    [Fact]
    public Task Cancel_StopsBetweenFiles() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var vm = await OpenEditorAsync(c, "Alpha", "Bravo", "Charlie");
        Field(vm, TagField.Album).Text = "Stop me";
        await vm.PreviewCommand.ExecuteAsync(null);

        // Cancel as soon as the first file has been reported done.
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TagEditorViewModel.ProgressDone) && vm.ProgressDone >= 1) vm.CancelWritingCommand.Execute(null);
        };
        await vm.WriteCommand.ExecuteAsync(null);

        Assert.Equal(TagEditorStep.Done, vm.Step);
        Assert.Contains("cancelled", vm.ResultSummary);
        var changed = new[] { "a.mp3", "b.mp3", "c.mp3" }.Count(n =>
        {
            using var f = TagLib.File.Create(Path.Combine(c.App.MusicDir, n));
            return f.Tag.Album == "Stop me";
        });
        Assert.InRange(changed, 1, 2); // at least the first, never all: and each finished file is complete
    });

    [Fact]
    public Task Close_IsIgnored_WhileWriting_AndWorksOtherwise() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var vm = await OpenEditorAsync(c, "Alpha");
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;

        vm.CloseCommand.Execute(null);
        Assert.Equal(1, closed);

        vm.Step = TagEditorStep.Writing;
        vm.CloseCommand.Execute(null);
        Assert.Equal(1, closed); // not while writing
    });

    [Fact]
    public Task TagEditorWindow_HasNoBrokenBindings_InAnyStep() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var vm = await OpenEditorAsync(c, "Alpha", "Bravo");
        var window = new TagEditorWindow(vm);
        window.Show();
        try
        {
            var broken = new List<string>();
            async Task Audit(string step)
            {
                await Task.Delay(150);
                window.UpdateLayout();
                broken.AddRange(BindingAudit.FindBroken(window).Select(b => $"[{step}] {b}"));
            }

            Field(vm, TagField.Year).Text = "19"; // an error state is visible too
            await Audit("edit+error");

            Field(vm, TagField.Year).Text = "1990";
            Field(vm, TagField.Album).Text = "Audit";
            await vm.PreviewCommand.ExecuteAsync(null);
            await Audit("preview");

            await vm.WriteCommand.ExecuteAsync(null);
            await Audit("done");

            Assert.True(broken.Count == 0, "Broken bindings:\n" + string.Join("\n", broken.Distinct()));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task TheWindow_CannotBeClosed_WhileWriting() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var vm = await OpenEditorAsync(c, "Alpha");
        var window = new TagEditorWindow(vm);
        window.Show();
        vm.Step = TagEditorStep.Writing;

        window.Close();
        Assert.True(window.IsVisible);       // the close was vetoed

        vm.Step = TagEditorStep.Done;
        window.Close();
        Assert.False(window.IsVisible);
    });
}
