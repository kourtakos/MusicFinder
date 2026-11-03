using System.Text;
using Microsoft.Extensions.DependencyInjection;
using MusicFinder.App.Services;
using MusicFinder.App.ViewModels;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.UiSmokeTests;

/// <summary>Settings > DJ history: where each program's history is read from, "Import now", and the page's bindings.</summary>
public class HistoryImportUiTests
{
    private static readonly (string, string, string, string)[] Files = [("a.mp3", "Alpha", "Artist", "Pop"), ("b.mp3", "Bravo", "Artist", "Pop")];

    private static async Task<(UiTestApp App, FakeDialogs Dialogs)> StartAsync()
    {
        var dialogs = new FakeDialogs();
        var app = await UiTestApp.CreateWithFilesAsync(s => s.AddSingleton<IUserDialogs>(dialogs), Files);
        app.Window.Show();
        await app.Main.InitializeAsync();
        await Ui.WaitUntilAsync(() => app.Main.Library.Search.Results.Count == 2, what: "indexed rows");
        return (app, dialogs);
    }

    private static string NewHistoryFolder() => Path.Combine(Path.GetTempPath(), "mf-ui-vdj-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public Task BothPrograms_AreListed_AndTraktorIsNeverPromisedAsLive() => Ui.RunAsync(async () =>
    {
        var (app, _) = await StartAsync();
        await using var _a = app;

        var programs = app.Main.Settings.History.Programs;

        Assert.Equal(["VirtualDJ", "Traktor"], programs.Select(p => p.DisplayName));
        Assert.Equal("Read while you play", programs[0].SyncText);
        Assert.Equal("Sync after export/close", programs[1].SyncText);
        Assert.True(app.Settings.Load().HistoryAutoImport);
    });

    [Fact]
    public Task Browse_SavesTheChosenFolder_AndUseDetectedForgetsIt() => Ui.RunAsync(async () =>
    {
        var (app, dialogs) = await StartAsync();
        await using var _a = app;
        var folder = NewHistoryFolder();
        Directory.CreateDirectory(folder);
        var vdj = app.Main.Settings.History.Programs[0];
        try
        {
            dialogs.FolderPicked = folder;
            vdj.BrowseCommand.Execute(null);

            Assert.Equal(folder, app.Settings.Load().VirtualDjHistoryFolder);
            Assert.Equal(folder, vdj.FolderText);
            Assert.True(vdj.HasChosenFolder);
            Assert.Equal("The folder you chose.", vdj.FolderHint);
            Assert.Null(app.Settings.Load().TraktorHistoryFolder);          // the other program is untouched

            dialogs.FolderPicked = null;                                     // the user cancels the folder dialog
            vdj.BrowseCommand.Execute(null);
            Assert.Equal(folder, app.Settings.Load().VirtualDjHistoryFolder);

            vdj.UseDetectedCommand.Execute(null);
            Assert.Null(app.Settings.Load().VirtualDjHistoryFolder);
            Assert.False(vdj.HasChosenFolder);
        }
        finally { Directory.Delete(folder, true); }
    });

    [Fact]
    public Task ImportNow_ReadsTheChosenFolder_AndShowsWhatHappened() => Ui.RunAsync(async () =>
    {
        var (app, dialogs) = await StartAsync();
        await using var _a = app;
        var folder = NewHistoryFolder();
        Directory.CreateDirectory(folder);
        try
        {
            var played = DateTimeOffset.UtcNow.AddMinutes(-2);
            var known = Path.Combine(app.MusicDir, "a.mp3");
            File.WriteAllText(Path.Combine(folder, $"{played:yyyy-MM-dd}.m3u"),
                $"#EXTVDJ:<time>{played.ToLocalTime():HH:mm}</time><lastplaytime>{played.ToUnixTimeSeconds()}</lastplaytime><artist>Artist</artist><title>Alpha</title>\r\n{known}\r\n" +
                $"#EXTVDJ:<time>{played.ToLocalTime():HH:mm}</time><lastplaytime>{played.ToUnixTimeSeconds() + 1}</lastplaytime><artist>X</artist><title>Y</title>\r\nD:\\Not\\Here.mp3\r\n",
                new UTF8Encoding(false));
            dialogs.FolderPicked = folder;
            var vdj = app.Main.Settings.History.Programs[0];
            vdj.BrowseCommand.Execute(null);

            await vdj.ImportNowCommand.ExecuteAsync(null);
            await Ui.WaitUntilAsync(() => vdj.ResultText.Contains("not matched"), what: "the result line");

            Assert.False(vdj.ResultIsError);
            Assert.StartsWith("1 new", vdj.ResultText);
            Assert.Contains("1 not matched", vdj.ResultText);
            await Ui.WaitUntilAsync(() => app.Main.Settings.History.PendingText.StartsWith("1 play "), what: "the waiting count");

            // The same click again finds nothing new: the file did not change.
            await vdj.ImportNowCommand.ExecuteAsync(null);
            await Ui.WaitUntilAsync(() => vdj.ResultText == "Nothing new.", what: "the second result");
        }
        finally { Directory.Delete(folder, true); }
    });

    [Fact]
    public Task AMissingFolder_IsReportedAsAnError_NotAsSuccess() => Ui.RunAsync(async () =>
    {
        var (app, dialogs) = await StartAsync();
        await using var _a = app;
        var gone = NewHistoryFolder();
        dialogs.FolderPicked = gone;
        var vdj = app.Main.Settings.History.Programs[0];
        vdj.BrowseCommand.Execute(null);

        await vdj.ImportNowCommand.ExecuteAsync(null);
        await Ui.WaitUntilAsync(() => vdj.ResultIsError, what: "the error");

        Assert.Contains("does not exist", vdj.ResultText);
    });

    [Fact]
    public Task TheAutoSwitch_IsSaved() => Ui.RunAsync(async () =>
    {
        var (app, _) = await StartAsync();
        await using var _a = app;

        app.Main.Settings.History.AutoImport = false;
        Assert.False(app.Settings.Load().HistoryAutoImport);

        app.Main.Settings.History.AutoImport = true;
        Assert.True(app.Settings.Load().HistoryAutoImport);
    });

    [Fact]
    public Task TheSettingsPage_HasNoBrokenBinding_WithTheHistorySection() => Ui.RunAsync(async () =>
    {
        var (app, _) = await StartAsync();
        await using var _a = app;
        app.Main.SelectedTab = AppTab.Settings;
        await Task.Delay(500);

        Assert.Empty(BindingAudit.FindBroken(app.Window));
    });
}
