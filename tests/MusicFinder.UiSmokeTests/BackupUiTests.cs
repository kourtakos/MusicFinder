using Microsoft.Extensions.DependencyInjection;
using System.IO.Compression;
using System.Text.Json;
using MusicFinder.App.Services;
using MusicFinder.App.ViewModels;
using MusicFinder.App.Views;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure.Backup;

namespace MusicFinder.UiSmokeTests;

/// <summary>Backup and restore through the Settings section and the restore window, on a temp database. Nothing is swapped: the restart is a fake.</summary>
public class BackupUiTests
{
    private sealed class FakeRestarter : IAppRestarter
    {
        public int Restarts { get; private set; }
        public void Restart() => Restarts++;
    }

    private static readonly (string, string, string, string)[] Files =
    [
        ("a.mp3", "Alpha", "Artist", "Pop"),
        ("b.mp3", "Bravo", "Artist", "Pop"),
    ];

    private sealed record Ctx(UiTestApp App, FakeDialogs Dialogs, FakeRestarter Restarter)
    {
        public BackupSettingsViewModel Backup => App.Main.Settings.Backup;
    }

    private static async Task<Ctx> StartAsync()
    {
        var dialogs = new FakeDialogs();
        var restarter = new FakeRestarter();
        var app = await UiTestApp.CreateWithFilesAsync(s =>
        {
            s.AddSingleton<IUserDialogs>(dialogs);
            s.AddSingleton<IAppRestarter>(restarter);
        }, Files);
        app.Window.Show();
        await app.Main.InitializeAsync();
        await Ui.WaitUntilAsync(() => app.Main.Library.Search.Results.Count == 2, what: "indexed rows");
        return new Ctx(app, dialogs, restarter);
    }

    private static string TempArchive() => Path.Combine(Path.GetTempPath(), "mf-ui-backup-" + Guid.NewGuid().ToString("N") + LibraryBackupService.FileExtension);

    [Fact]
    public Task BackupNow_WritesAVerifiedArchive_WithoutSecretsByDefault_AndSaysItHasNoMusic() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var target = TempArchive();
        c.Dialogs.SavePicked = target;
        try
        {
            Assert.False(c.Backup.IncludeSecrets);
            Assert.False(c.Backup.IncludeArtwork);

            await c.Backup.BackupNowCommand.ExecuteAsync(null);

            Assert.True(File.Exists(target));
            Assert.Contains("verified", c.Backup.StatusText);
            Assert.Contains("does not contain your music files", c.Backup.StatusText);
            var manifest = await c.App.Service<MusicFinder.Domain.Contracts.ILibraryBackupService>().InspectAsync(target, default);
            Assert.Equal(2, manifest.Counts["Tracks"]);
            Assert.False(manifest.IncludesSecrets);
        }
        finally { File.Delete(target); }
    });

    [Fact]
    public Task TheSettingsSection_HasNoBrokenBinding() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.App.Main.SelectedTab = AppTab.Settings;
        await Task.Delay(300);

        Assert.Empty(BindingAudit.FindBroken(c.App.Window));
        Assert.Contains("does not contain your music files", c.Backup.NotTheMusicText);
    });

    [Fact]
    public Task Restore_ShowsThePreview_NeedsTheTickBox_StagesWithASafetyBackup_AndLeavesTheLibraryAlone() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var archive = TempArchive();
        c.Dialogs.SavePicked = archive;
        try
        {
            await c.Backup.BackupNowCommand.ExecuteAsync(null);
            c.Dialogs.FilePicked = archive;

            await c.Backup.RestoreCommand.ExecuteAsync(null);

            var vm = Assert.IsType<RestoreViewModel>(c.Dialogs.Restore);
            Assert.Contains(vm.CountLines, l => l.StartsWith("Tracks: 2 in the backup, 2 now"));
            var mapping = Assert.Single(vm.Mappings);
            Assert.False(mapping.IsChanged);
            Assert.False(vm.CanRestore);                                       // not without the confirmation

            var window = new RestoreWindow(vm) { Owner = c.App.Window };
            window.Show();
            try
            {
                await Task.Delay(300);
                Assert.Empty(BindingAudit.FindBroken(window));
            }
            finally { window.Close(); }

            vm.Confirmed = true;
            Assert.True(vm.CanRestore);
            await vm.RestoreCommand.ExecuteAsync(null);

            Assert.True(vm.IsStaged);
            Assert.Contains("Restart MusicFinder", vm.StatusText);
            Assert.True(c.App.Service<MusicFinder.Domain.Contracts.ILibraryRestoreService>().HasPendingRestore);
            Assert.Equal(2, c.App.Main.Library.Search.Results.Count);          // the live library is untouched
            vm.RestartNowCommand.Execute(null);
            Assert.Equal(1, c.Restarter.Restarts);

            c.Backup.RefreshState();
            Assert.True(c.Backup.HasPending);
            c.Backup.CancelPendingCommand.Execute(null);
            Assert.False(c.Backup.HasPending);
        }
        finally { File.Delete(archive); }
    });

    [Fact]
    public Task AChangedFolder_BecomesAMapping_AndABackupFromANewerVersionCannotBeRestored() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var archive = TempArchive();
        c.Dialogs.SavePicked = archive;
        try
        {
            await c.Backup.BackupNowCommand.ExecuteAsync(null);
            c.Dialogs.FilePicked = archive;
            await c.Backup.RestoreCommand.ExecuteAsync(null);
            var vm = Assert.IsType<RestoreViewModel>(c.Dialogs.Restore);
            var row = vm.Mappings[0];
            row.NewPath = Path.Combine(Path.GetTempPath(), "somewhere-else");
            Assert.True(row.IsChanged);
            Assert.Equal("Not found on this PC", row.FoundText);

            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
            {
                BackupManifest m;
                using (var r = zip.GetEntry("manifest.json")!.Open()) m = JsonSerializer.Deserialize<BackupManifest>(r)!;
                m.SchemaVersion = 999;
                zip.GetEntry("manifest.json")!.Delete();
                using var w = zip.CreateEntry("manifest.json").Open();
                JsonSerializer.Serialize(w, m);
            }
            c.Dialogs.FilePicked = archive;
            await c.Backup.RestoreCommand.ExecuteAsync(null);

            var future = Assert.IsType<RestoreViewModel>(c.Dialogs.Restore);
            Assert.True(future.IsRefused);
            future.Confirmed = true;
            Assert.False(future.CanRestore);
            Assert.Contains("newer MusicFinder", future.StatusText);
        }
        finally { File.Delete(archive); }
    });

    [Fact]
    public Task ADamagedBackup_IsRejectedWithAReason_AndNoRestoreWindowOpens() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var junk = TempArchive();
        File.WriteAllText(junk, "not a backup");
        try
        {
            c.Dialogs.FilePicked = junk;

            await c.Backup.RestoreCommand.ExecuteAsync(null);

            Assert.Null(c.Dialogs.Restore);
            Assert.Contains("cannot be used", c.Backup.StatusText);
        }
        finally { File.Delete(junk); }
    });
}
