using System.ComponentModel;
using System.IO.Compression;
using Microsoft.Extensions.DependencyInjection;
using MusicFinder.App.Services;
using MusicFinder.App.ViewModels;
using MusicFinder.App.Views;
using MusicFinder.Application.Identification;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure.Identification;

namespace MusicFinder.UiSmokeTests;

/// <summary>The welcome tour and the song-recognition setup, with fakes for fpcalc, AcoustID and the Downloads folder: nothing real is started or contacted.</summary>
public sealed class FirstRunUiTests : IDisposable
{
    private sealed class FakeRunner : IProcessRunner
    {
        public string Output { get; set; } = "fpcalc version 1.6.2";
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(new ProcessResult(0, Output, "", false));
    }

    private sealed class FakeFingerprints : IFingerprintService
    {
        public FingerprinterInfo? Locate() => new(@"C:\fake\fpcalc.exe", "test");
        public Task<FingerprintResult> ComputeAsync(string path, CancellationToken ct) => Task.FromResult(new FingerprintResult("AQAAfp", 200, 2, 120));
    }

    private sealed class FakeAcoustId : IAcoustIdClient
    {
        public Exception? Throws { get; set; }
        public List<string> Keys { get; } = [];
        public Task<IReadOnlyList<AcoustIdMatch>> LookupAsync(string apiKey, string fingerprint, int durationSeconds, CancellationToken ct)
        {
            Keys.Add(apiKey);
            return Throws is null ? Task.FromResult<IReadOnlyList<AcoustIdMatch>>([]) : throw Throws;
        }
    }

    private sealed class FakeDownloads(string? path) : IDownloadsFolder
    {
        public string? Path { get; } = path;
    }

    private sealed record Ctx(UiTestApp App, FakeDialogs Dialogs, FakeAcoustId Acoustid, string Downloads)
    {
        public FirstRunViewModel Vm => App.Service<FirstRunViewModel>();
        public AppSettings Saved => App.Settings.Load();
    }

    private static readonly byte[] Program = [(byte)'M', (byte)'Z', 0x90, 0, 3, 0, 0, 0];
    private readonly List<string> _temp = [];

    public void Dispose()
    {
        foreach (var d in _temp) try { Directory.Delete(d, true); } catch (IOException) { /* temp */ }
    }

    private string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "mf-firstrun-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        _temp.Add(d);
        return d;
    }

    private async Task<Ctx> StartAsync()
    {
        var dialogs = new FakeDialogs();
        var acoustid = new FakeAcoustId();
        var downloads = TempDir();
        var app = await UiTestApp.CreateAsync(configure: s =>
        {
            s.AddSingleton<IUserDialogs>(dialogs);
            s.AddSingleton<IProcessRunner>(new FakeRunner());
            s.AddSingleton<IAcoustIdClient>(acoustid);
            s.AddSingleton<IFingerprintService>(new FakeFingerprints());
            s.AddSingleton<IDownloadsFolder>(new FakeDownloads(downloads));
        });
        app.Window.Show();
        return new Ctx(app, dialogs, acoustid, downloads);
    }

    private static string Exe(string folder, string name = "fpcalc.exe")
    {
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, Program);
        return path;
    }

    // ===== the tour =====

    [Fact]
    public Task TheTour_WalksFromWelcomeToTheSetupPage_AndDoneRemembersIt() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var vm = c.Vm;
        vm.Open(toRecognition: false);

        Assert.True(vm.IsWelcome);
        Assert.False(vm.CanGoBack);
        Assert.Equal("Next", vm.NextText);
        Assert.False(c.Saved.FirstRunCompleted);

        for (var i = 1; i <= FirstRunViewModel.Steps.Count; i++)
        {
            vm.NextCommand.Execute(null);
            Assert.True(vm.IsTourStep);
            Assert.Equal(FirstRunViewModel.Steps[i - 1].Title, vm.CurrentStep.Title);
            Assert.True(vm.CanGoBack);
        }
        vm.NextCommand.Execute(null);
        Assert.True(vm.IsRecognition);
        Assert.Equal("Done", vm.NextText);
        Assert.Equal($"{vm.PageCount} of {vm.PageCount}", vm.PageText);

        vm.BackCommand.Execute(null);
        Assert.True(vm.IsTourStep);
        vm.NextCommand.Execute(null);

        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;
        vm.NextCommand.Execute(null);                                    // Done
        Assert.Equal(1, closed);
        Assert.True(c.Saved.FirstRunCompleted);
    });

    [Fact]
    public Task Skip_AlsoMeansTheTourIsNotShownAgainByItself() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.Vm.Open(false);

        c.Vm.CloseCommand.Execute(null);

        Assert.True(c.Saved.FirstRunCompleted);
    });

    [Fact]
    public void TheTourText_NamesOnlyThingsThatExist()
    {
        var all = string.Join("\n", FirstRunViewModel.Steps.Select(s => s.Body));

        foreach (var mention in new[] { "Find similar", "Analyze BPM and key", "Ctrl+B", "Ctrl+Alt+Space", "Session tab", "DJ history" })
            Assert.Contains(mention, all);
        Assert.DoesNotContain("playlist", all, StringComparison.OrdinalIgnoreCase);       // there are none yet; the tour must not promise them
        Assert.DoesNotContain("crate", all, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public Task TheSettingsButtons_OpenTheTourOrTheSetupPage() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var settings = c.App.Main.Settings;

        settings.ShowTourCommand.Execute(null);
        Assert.Same(c.Vm, c.Dialogs.FirstRun);
        Assert.True(c.Vm.IsWelcome);

        settings.SetUpRecognitionCommand.Execute(null);
        Assert.True(c.Vm.IsRecognition);
    });

    // ===== fpcalc =====

    [Fact]
    public Task SavingFpcalc_MakesItReady_AndShowsItsVersion() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var vm = c.Vm;
        vm.Open(true);
        Assert.False(vm.FpcalcReady);
        Assert.Equal("Not found yet.", vm.FpcalcStatus);

        await vm.ImportAsync(Exe(c.Downloads));
        await Ui.WaitUntilAsync(() => vm.FpcalcStatus.Contains("version 1.6.2"), what: "the version");

        Assert.True(vm.FpcalcReady);
        Assert.False(vm.FpcalcMessageIsError);
        Assert.Contains("1.6.2 is ready", vm.FpcalcMessage);
        Assert.Contains("tools", vm.FpcalcStatus);
        Assert.True(File.Exists(Path.Combine(c.Downloads, "fpcalc.exe")));          // the user's own download is left alone
        Assert.Contains("Found:", c.App.Main.Settings.Identification.FingerprinterStatus);   // the Settings page learns about it too
    });

    [Fact]
    public Task AWrongFile_IsExplained_AndFpcalcStaysMissing() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.Vm.Open(true);

        await c.Vm.ImportAsync(Exe(c.Downloads, "ffmpeg.exe"));

        Assert.False(c.Vm.FpcalcReady);
        Assert.True(c.Vm.FpcalcMessageIsError);
        Assert.Contains("named fpcalc.exe", c.Vm.FpcalcMessage);
    });

    [Fact]
    public Task AZipInDownloads_IsOfferedAndOneClickUsesIt() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var zipPath = Path.Combine(c.Downloads, "chromaprint-fpcalc-1.6.2-windows-x86_64.zip");
        using (var fs = File.Create(zipPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        using (var entry = zip.CreateEntry("chromaprint-fpcalc-1.6.2-windows-x86_64/fpcalc.exe").Open())
            entry.Write(Program);
        c.Vm.Open(true);

        var offered = Assert.Single(c.Vm.Candidates);
        Assert.True(offered.IsZip);
        Assert.StartsWith("Use the zip", offered.Label);

        await c.Vm.UseCandidateCommand.ExecuteAsync(offered);

        Assert.True(c.Vm.FpcalcReady);
        Assert.Empty(c.Vm.Candidates);                                             // nothing to offer once it is saved
    });

    [Fact]
    public Task TheTwoDownloadPages_OpenInTheBrowser() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;

        c.Vm.OpenFpcalcPageCommand.Execute(null);
        c.Vm.OpenKeyPageCommand.Execute(null);

        Assert.Equal(["https://acoustid.org/chromaprint", "https://acoustid.org/new-application"], c.Dialogs.UrlsOpened);
    });

    // ===== the key =====

    [Fact]
    public Task APastedKey_IsSavedWhenItLooksRight_AndNeverWhenItDoesNot() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var vm = c.Vm;
        vm.Open(true);

        vm.KeyText = "https://acoustid.org/new-application";
        Assert.False(vm.KeyIsValid);
        Assert.Contains("web address", vm.KeyProblem);
        Assert.Null(c.Saved.AcoustIdApiKey);

        vm.KeyText = "  abcd1234 ";
        Assert.True(vm.KeyIsValid);
        Assert.Equal("", vm.KeyProblem);
        Assert.Equal("abcd1234", c.Saved.AcoustIdApiKey);
        Assert.Equal("abcd1234", c.App.Main.Settings.Identification.ApiKey);          // the Settings page shows the same key

        vm.KeyText = "abc d";                                                          // a bad edit does not replace the saved key
        Assert.Contains("no spaces", vm.KeyProblem);
        Assert.Equal("abcd1234", c.Saved.AcoustIdApiKey);

        vm.KeyText = "";
        Assert.Null(c.Saved.AcoustIdApiKey);
        Assert.Equal("", vm.KeyProblem);
    });

    [Fact]
    public Task TestTheKey_NeedsFpcalcFirst_AndSendsNothingWithout() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.Vm.Open(true);
        c.Vm.KeyText = "abcd1234";

        await c.Vm.TestKeyCommand.ExecuteAsync(null);

        Assert.False(c.Vm.KeyResultOk);
        Assert.Contains("Add fpcalc first", c.Vm.KeyResult);
        Assert.Empty(c.Acoustid.Keys);
        Assert.Empty(c.Dialogs.ConfirmMessages);
    });

    [Fact]
    public Task TestTheKey_ShowsExactlyWhatIsSent_AndOnlyThenAsks() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.Vm.Open(true);
        await c.Vm.ImportAsync(Exe(c.Downloads));
        c.Vm.KeyText = "abcd1234";
        var song = Path.Combine(c.Downloads, "My Song.mp3");
        File.WriteAllBytes(song, [1]);
        c.Dialogs.FilePicked = song;

        await c.Vm.TestKeyCommand.ExecuteAsync(null);

        var message = Assert.Single(c.Dialogs.ConfirmMessages);
        Assert.Contains("\"My Song.mp3\"", message);
        Assert.Contains("not the audio", message);
        Assert.Contains("api.acoustid.org", message);
        Assert.True(c.Vm.KeyResultOk);
        Assert.Equal(["abcd1234"], c.Acoustid.Keys);
    });

    [Fact]
    public Task DecliningTheQuestion_SendsNothing() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.Vm.Open(true);
        await c.Vm.ImportAsync(Exe(c.Downloads));
        c.Vm.KeyText = "abcd1234";
        c.Dialogs.FilePicked = Path.Combine(c.Downloads, "song.mp3");
        c.Dialogs.ConfirmAnswer = false;

        await c.Vm.TestKeyCommand.ExecuteAsync(null);

        Assert.Empty(c.Acoustid.Keys);
        Assert.Equal("", c.Vm.KeyResult);
    });

    [Fact]
    public Task APickCancelled_SendsNothing_AndAWrongKeyIsReportedAsRejected() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.Vm.Open(true);
        await c.Vm.ImportAsync(Exe(c.Downloads));
        c.Vm.KeyText = "abcd1234";

        c.Dialogs.FilePicked = null;                                                   // the user cancels the file dialog
        await c.Vm.TestKeyCommand.ExecuteAsync(null);
        Assert.Empty(c.Acoustid.Keys);

        c.Dialogs.FilePicked = Path.Combine(c.Downloads, "song.mp3");
        c.Acoustid.Throws = new IdentificationHttpException(IdentificationFailure.ServerError, 400, "AcoustID: invalid API key");
        await c.Vm.TestKeyCommand.ExecuteAsync(null);

        Assert.False(c.Vm.KeyResultOk);
        Assert.Contains("did not accept the key", c.Vm.KeyResult);
    });

    // ===== the window =====

    [Fact]
    public Task EveryPageOfTheWindow_HasNoBrokenBinding_AndTheCloseButtonRemembersTheTour() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.Vm.Open(false);
        var window = new FirstRunWindow(c.Vm) { Owner = c.App.Window };
        window.Show();
        try
        {
            foreach (var page in new[] { 0, 1, c.Vm.LastPage })
            {
                c.Vm.PageIndex = page;
                await Task.Delay(150);
                Assert.Empty(BindingAudit.FindBroken(window));
            }
            Assert.False(c.Saved.FirstRunCompleted);
        }
        finally { window.Close(); }
        Assert.True(c.Saved.FirstRunCompleted);                                        // closing the window by its own button counts
    });
}
