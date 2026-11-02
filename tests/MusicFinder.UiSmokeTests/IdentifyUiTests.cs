using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using MusicFinder.App;
using MusicFinder.App.Services;
using MusicFinder.App.ViewModels;
using MusicFinder.App.Views;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.UiSmokeTests;

/// <summary>The identification service, replaced: scripted answers, no network, no fpcalc.</summary>
internal sealed class FakeIdentification : IIdentificationService
{
    public const string RecordingId = "5f2c9a0e-7b1d-4c3a-9e84-0d6b2a1f7c33";
    private readonly ISettingsStore _settings;

    public FakeIdentification(ISettingsStore settings) => _settings = settings;

    public FingerprinterInfo? Fingerprinter { get; set; } = new(@"C:\Tools\fpcalc.exe", "settings");
    public Func<IdentificationRequest, IdentificationResult>? Respond { get; set; }
    public TimeSpan Delay { get; set; }
    public List<IdentificationRequest> Requests { get; } = [];
    public List<string> ReleaseRequests { get; } = [];
    public int CacheCleared { get; private set; }

    public static IdentificationCandidate Candidate(string title = "Ξανά στη ζωή μου", string artist = "Χάρις Αλεξίου", double score = 0.97) => new(
        RecordingId, title, artist, 225_000, IdentificationMethod.AcoustIdFingerprint, score, null,
        ["duration matches (3:45)", "title differs from your tag"],
        [new ReleaseSummary("rel-compilation", "Greatest Hits", "1999", "GR", "Official", "rg-2", "Album", ["Compilation"]),
         new ReleaseSummary("rel-1", "Το Τραγούδι", "1985", "GR", "Official", "rg-1", "Album", [])],
        ["laiko"], []);

    public IdentificationReadiness GetReadiness()
    {
        var s = _settings.Load();
        return new(s.IdentificationEnabled, s.IdentificationConsentUtc is not null, !string.IsNullOrWhiteSpace(s.AcoustIdApiKey),
            !string.IsNullOrWhiteSpace(s.MusicBrainzContact), Fingerprinter);
    }

    public async Task<IdentificationResult> IdentifyAsync(IdentificationRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        if (Delay > TimeSpan.Zero)
        {
            try { await Task.Delay(Delay, ct); }
            catch (OperationCanceledException) { return IdentificationResult.Failed(request.TrackId, request.Method, IdentificationOutcome.Cancelled, "Cancelled."); }
        }
        return Respond?.Invoke(request) ?? new IdentificationResult(request.TrackId, IdentificationMethod.AcoustIdFingerprint,
            IdentificationOutcome.Found, [Candidate()], null, false, DateTime.UtcNow);
    }

    public async Task<IReadOnlyList<IdentificationCandidate>> IdentifyAsync(long trackId, CancellationToken ct) =>
        (await IdentifyAsync(new IdentificationRequest(trackId), ct)).Candidates;

    public Task<ReleaseDetails?> GetReleaseDetailsAsync(string releaseId, CancellationToken ct)
    {
        ReleaseRequests.Add(releaseId);
        return Task.FromResult<ReleaseDetails?>(releaseId == "rel-1"
            ? new ReleaseDetails("rel-1", "Το Τραγούδι", "1985-03-02", "GR", "Χάρις Αλεξίου", [],
                [new ReleaseTrack(RecordingId, 1, 5, 9, "Ξανά στη ζωή μου", "Χάρις Αλεξίου")])
            : new ReleaseDetails(releaseId, "Greatest Hits", "1999", "GR", "Various", [],
                [new ReleaseTrack(RecordingId, 1, 12, 20, "Ξανά στη ζωή μου", "Χάρις Αλεξίου")]));
    }

    public List<string> Enriched { get; } = [];
    public Func<IdentificationCandidate, IdentificationCandidate>? Enrich { get; set; }

    public Task<IdentificationCandidate> EnrichAsync(IdentificationCandidate candidate, CancellationToken ct)
    {
        Enriched.Add(candidate.RecordingId);
        return Task.FromResult(Enrich?.Invoke(candidate) ?? candidate);
    }

    public Task ClearCacheAsync(CancellationToken ct)
    {
        CacheCleared++;
        return Task.CompletedTask;
    }
}

/// <summary>The Cover Art Archive, replaced: a cover for one release, none for another.</summary>
internal sealed class FakeCoverArt : ICoverArtClient
{
    public List<string> Requests { get; } = [];
    public static CoverImage Image() => MusicFinder.Application.Tags.CoverImageReader.TryRead(Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="))!;

    public Task<CoverImage?> GetFrontAsync(string releaseId, CancellationToken ct)
    {
        Requests.Add(releaseId);
        return Task.FromResult<CoverImage?>(releaseId == "rel-1" ? Image() : null);
    }
}

public class IdentifyUiTests
{
    private static readonly (string, string, string, string)[] Files =
    [
        ("a.mp3", "unknown track", "Artist One", "Disco"),
        ("b.mp3", "Bravo", "Artist One", "Funk"),
        ("c.mp3", "Charlie", "Artist Two", "Disco"),
    ];

    private sealed record Ctx(UiTestApp App, FakeDialogs Dialogs, FakeIdentification Service, FakeCoverArt Covers)
    {
        public SearchViewModel Search => App.Main.Library.Search;
        public AppSettings Saved => App.Settings.Load();
    }

    private static async Task<Ctx> StartAsync(bool agreed = true)
    {
        var dialogs = new FakeDialogs();
        FakeIdentification? fake = null;
        var covers = new FakeCoverArt();
        var app = await UiTestApp.CreateWithFilesAsync(s =>
        {
            s.AddSingleton<IUserDialogs>(dialogs);
            s.AddSingleton<IIdentificationService>(sp => fake = new FakeIdentification(sp.GetRequiredService<ISettingsStore>()));
            s.AddSingleton<ICoverArtClient>(covers);
        }, Files);
        app.Window.Show();
        await app.Main.InitializeAsync();
        await Ui.WaitUntilAsync(() => app.Main.Library.Search.Results.Count == 3, what: "indexed rows");
        _ = app.Service<IIdentificationService>();   // make sure the fake exists
        if (agreed)
        {
            var s = app.Settings.Load();
            s.IdentificationEnabled = true;
            s.IdentificationConsentUtc = DateTime.UtcNow;
            s.MusicBrainzContact = "dj@example.com";
            s.AcoustIdApiKey = "KEY123";
            app.Settings.Save(s);
        }
        return new Ctx(app, dialogs, fake!, covers);
    }

    private static IdentifyViewModel NewVm(Ctx c, IReadOnlyList<TrackRowViewModel> rows) =>
        new(rows, c.Service, c.App.Service<ITagEditingService>(), c.App.Service<ITrackRepository>(), c.Covers, c.App.Settings,
            c.App.Service<MusicFinder.Infrastructure.AppPaths>());

    private static TrackRowViewModel Row(Ctx c, string title) => c.Search.Results.Single(r => r.Title == title);

    // ===== consent =====

    [Fact]
    public Task TheFirstTime_TheUserIsToldWhatIsSent_AndDecliningChangesNothing() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync(agreed: false);
        await using var _ = c.App;
        c.Search.SetSelectedItems([Row(c, "unknown track")]);
        c.Dialogs.OnConsent = vm => vm.DeclineCommand.Execute(null);

        c.App.Main.Library.IdentifyCommand.Execute(null);

        var consent = Assert.IsType<IdentificationConsentViewModel>(c.Dialogs.Consent);
        Assert.Contains("fingerprint", consent.WhatIsSent);
        Assert.Contains("no file names", consent.WhatIsSent);
        Assert.Contains("non-commercial", consent.NonCommercial);
        Assert.Null(c.Dialogs.Identify);                                   // the lookup window never opened
        Assert.False(c.Saved.IdentificationEnabled);
        Assert.Null(c.Saved.IdentificationConsentUtc);
        Assert.Empty(c.Service.Requests);
    });

    [Fact]
    public Task Accepting_SavesTheContactAndKey_OpensTheWindow_AndTurnsTheSettingsOn() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync(agreed: false);
        await using var _ = c.App;
        c.Search.SetSelectedItems([Row(c, "unknown track")]);
        c.Dialogs.OnConsent = vm => { vm.Contact = "  dj@example.com "; vm.ApiKey = " KEY123 "; vm.AcceptCommand.Execute(null); };

        c.App.Main.Library.IdentifyCommand.Execute(null);

        Assert.NotNull(c.Dialogs.Identify);
        var saved = c.Saved;
        Assert.True(saved.IdentificationEnabled);
        Assert.NotNull(saved.IdentificationConsentUtc);
        Assert.Equal(("dj@example.com", "KEY123"), (saved.MusicBrainzContact, saved.AcoustIdApiKey));       // trimmed
        Assert.True(c.App.Main.Settings.Identification.Enabled);                                            // the Settings page follows
        Assert.Equal("KEY123", c.App.Main.Settings.Identification.ApiKey);
    });

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("(((")]
    public Task ABlankContact_IsRefused_AndNothingIsSaved(string contact) => Ui.RunAsync(async () =>
    {
        var c = await StartAsync(agreed: false);
        await using var _ = c.App;
        var vm = new IdentificationConsentViewModel(c.App.Settings, TimeProvider.System) { Contact = contact };

        vm.AcceptCommand.Execute(null);

        Assert.False(vm.Accepted);
        Assert.True(vm.HasError);
        Assert.Contains("contact", vm.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(c.Saved.IdentificationEnabled);
    });

    [Fact]
    public Task TheConsentWindow_HasNoBrokenBindings_AndTheKeyBoxFeedsTheViewModel() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync(agreed: false);
        await using var _ = c.App;
        var vm = new IdentificationConsentViewModel(c.App.Settings, TimeProvider.System);
        var window = new IdentificationConsentWindow(vm);
        window.Show();
        await Task.Delay(300);
        try
        {
            Assert.Empty(BindingAudit.FindBroken(window));
            var box = Ui.FindDescendant<PasswordBox>(window)!;
            box.Password = "SECRET";
            Assert.Equal("SECRET", vm.ApiKey);
        }
        finally { window.Close(); }
    });

    // ===== the window =====

    [Fact]
    public Task Identify_ListsTheCandidate_WithItsScoreNamed_AndChoosingAReleaseFillsTheProposals() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var vm = NewVm(c, [Row(c, "unknown track")]);

        await vm.StartAsync();
        await vm.WhenIdle;

        var candidate = Assert.Single(vm.Candidates);
        Assert.Equal(("Ξανά στη ζωή μου", "AcoustID score 0.97", "3:45"), (candidate.Title, candidate.ScoreText, candidate.DurationText));
        Assert.Contains("duration matches", candidate.EvidenceText);
        Assert.Same(candidate, vm.SelectedCandidate);                      // pre-selected, but nothing is applied yet
        // "No release" first, then Official albums without extra types before compilations.
        Assert.Equal(["No release (title, artist and id only)", "Το Τραγούδι · 1985 · GR · Album", "Greatest Hits · 1999 · GR · Album + Compilation"],
            vm.Releases.Select(r => r.Text));
        await vm.WhenIdle;
        Assert.Equal("Το Τραγούδι · 1985 · GR · Album", vm.SelectedRelease!.Text);

        var byField = vm.Proposals.ToDictionary(p => p.Field);
        Assert.Equal("5", byField[TagField.TrackNumber].Proposed);
        Assert.Equal("1985", byField[TagField.Year].Proposed);
        Assert.Equal("Το Τραγούδι", byField[TagField.Album].Proposed);
        Assert.False(byField[TagField.Title].Selected);                    // the file's own title differs: not ticked for the user
        Assert.False(byField[TagField.Genre].Selected);
        Assert.True(byField[TagField.MusicBrainzRecordingId].Selected);
        Assert.DoesNotContain(vm.Proposals, p => p.Field is TagField.Bpm or TagField.Key);

        vm.SelectedRelease = vm.Releases[2];
        await vm.WhenIdle;
        Assert.Equal("12", vm.Proposals.Single(p => p.Field == TagField.TrackNumber).Proposed);   // the other release says track 12
    });

    [Fact]
    public Task FromLookupToFile_TheTableIsThePreview_OneWriteButton_AndTheGridFollows() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var path = Path.Combine(c.App.MusicDir, "a.mp3");
        var vm = NewVm(c, [Row(c, "unknown track")]);
        await vm.StartAsync();
        await vm.WhenIdle;
        await vm.WhenIdle;
        vm.Proposals.Single(p => p.Field == TagField.Title).Selected = true;         // the user decides to take the title

        // The table shows now and proposed side by side, and the summary says what Write will do; nothing is written yet.
        var title = vm.Proposals.Single(p => p.Field == TagField.Title);
        Assert.Equal(("unknown track", "Ξανά στη ζωή μου"), (title.Current, title.Proposed));
        Assert.StartsWith("Will write: ", vm.ChangeSummary);
        Assert.Contains("Title", vm.ChangeSummary);
        using (var untouched = TagLib.File.Create(path)) Assert.Equal("unknown track", untouched.Tag.Title);

        vm.WriteCommand.Execute(null);
        await Ui.WaitUntilAsync(() => vm.Step == IdentifyStep.Done, what: "written");

        Assert.Contains("1 file written", vm.ResultSummary);
        Assert.Equal("Written", vm.Queue[0].Status);
        using (var f = TagLib.File.Create(path))
        {
            Assert.Equal("Ξανά στη ζωή μου", f.Tag.Title);
            Assert.Equal("Original Album", f.Tag.Album);                               // had a value, so it was not ticked and stays
            Assert.Equal(1985u, f.Tag.Year);
            Assert.Equal(5u, f.Tag.Track);
            Assert.Equal(FakeIdentification.RecordingId, f.Tag.MusicBrainzTrackId);
            Assert.Equal(["Disco"], f.Tag.Genres);                                   // the genre suggestion was never ticked
        }
        await Ui.WaitUntilAsync(() => c.Search.Results.Any(r => r.Title == "Ξανά στη ζωή μου"), what: "grid refreshed");
    });

    [Fact]
    public Task NothingTicked_CannotBeWritten() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var vm = NewVm(c, [Row(c, "unknown track")]);
        await vm.StartAsync();
        await vm.WhenIdle;
        await vm.WhenIdle;
        foreach (var p in vm.Proposals) p.Selected = false;
        vm.UseCover = false;

        vm.WriteCommand.Execute(null);
        await Task.Delay(100);

        Assert.Equal(IdentifyStep.Choose, vm.Step);
        Assert.Contains("Tick at least one", vm.Message);
    });

    [Fact]
    public Task ABatch_IsLookedUpInTurn_CanBeStopped_AndSkippedTracksAreMarked() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.Service.Delay = TimeSpan.FromMilliseconds(150);
        var vm = NewVm(c, [Row(c, "unknown track"), Row(c, "Bravo"), Row(c, "Charlie")]);
        Assert.True(vm.HasQueue);

        var run = vm.StartAsync();
        await Ui.WaitUntilAsync(() => vm.Queue[0].Status == "1 candidate", what: "first looked up");
        vm.CancelLookupsCommand.Execute(null);
        await run;

        Assert.Equal("1 candidate", vm.Queue[0].Status);
        Assert.Contains(vm.Queue[1].Status, new[] { "Cancelled", "Not looked up" });      // the one in flight, or not yet started
        Assert.Equal("Not looked up", vm.Queue[2].Status);
        Assert.False(vm.IsLookingUp);
        Assert.True(c.Service.Requests.Count < 3);
    });

    [Fact]
    public Task SkippingATrack_MovesToTheNextOne() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var vm = NewVm(c, [Row(c, "unknown track"), Row(c, "Bravo")]);
        await vm.StartAsync();
        await vm.WhenIdle;

        vm.SkipCommand.Execute(null);
        await vm.WhenIdle;

        Assert.Equal("Skipped", vm.Queue[0].Status);
        Assert.Same(vm.Queue[1], vm.Current);
        Assert.False(vm.HasNext);
    });

    [Theory]
    [InlineData(IdentificationOutcome.NoApiKey, "AcoustID API key")]
    [InlineData(IdentificationOutcome.FingerprinterMissing, "fpcalc.exe was not found")]
    [InlineData(IdentificationOutcome.Offline, "Could not reach")]
    [InlineData(IdentificationOutcome.NoMatch, "No recording")]
    public Task AProblem_IsShownInPlainEnglish_WithNoCandidates(IdentificationOutcome outcome, string text) => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.Service.Respond = r => IdentificationResult.Failed(r.TrackId, r.Method, outcome, text + " … details.");
        var vm = NewVm(c, [Row(c, "unknown track")]);

        await vm.StartAsync();
        await vm.WhenIdle;

        Assert.Empty(vm.Candidates);
        Assert.Contains(text, vm.Message);
        Assert.True(vm.HasMessage);
    });

    [Fact]
    public Task WhenNothingIsTurnedOn_TheWindowSaysSo_AndAsksNothing() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync(agreed: false);
        await using var _ = c.App;
        var vm = NewVm(c, [Row(c, "unknown track")]);

        await vm.StartAsync();

        Assert.Contains("off", vm.Message);
        Assert.Empty(c.Service.Requests);
    });

    [Fact]
    public Task SearchAgain_AsksWithoutUsingRememberedAnswers_AndWithTheChosenMethod() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var vm = NewVm(c, [Row(c, "unknown track")]);
        await vm.StartAsync();
        await vm.WhenIdle;

        vm.Method = vm.Methods.Single(m => m.Method == IdentificationMethod.MusicBrainzTagSearch);
        vm.SearchAgainCommand.Execute(null);
        await Ui.WaitUntilAsync(() => c.Service.Requests.Count == 2, what: "second request");

        Assert.False(c.Service.Requests[0].ForceRefresh);
        Assert.Equal((IdentificationMethod.MusicBrainzTagSearch, true), (c.Service.Requests[1].Method, c.Service.Requests[1].ForceRefresh));
        Assert.Contains("title, artist and album", vm.MethodNote);
    });

    [Fact]
    public Task TheWindow_HasNoBrokenBindings_InEveryStep() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var vm = NewVm(c, [Row(c, "unknown track"), Row(c, "Bravo")]);
        var window = new IdentifyWindow(vm);
        window.Show();
        try
        {
            await Ui.WaitUntilAsync(() => vm.Queue.All(q => q.Plan is not null), what: "list planned");
            Assert.Equal(IdentifyStep.Overview, vm.Step);
            await Task.Delay(300);
            Assert.Empty(BindingAudit.FindBroken(window));                  // the list

            vm.ReviewCommand.Execute(vm.Queue[0]);
            await Ui.WaitUntilAsync(() => vm.Step == IdentifyStep.Choose && vm.Proposals.Count > 0, what: "detail shown");
            await Task.Delay(300);
            Assert.Empty(BindingAudit.FindBroken(window));                  // one track in detail

            vm.BackToListCommand.Execute(null);
            vm.WriteCommand.Execute(null);
            await Ui.WaitUntilAsync(() => vm.Step == IdentifyStep.Done, what: "done");
            await Task.Delay(300);
            Assert.Empty(BindingAudit.FindBroken(window));
        }
        finally { window.Close(); }
    });

    // ===== settings and entry points =====

    [Fact]
    public Task TurningItOnInSettings_ShowsTheConsent_DecliningLeavesItOff_AcceptingTurnsItOn() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync(agreed: false);
        await using var _ = c.App;
        var settings = c.App.Main.Settings.Identification;
        Assert.False(settings.Enabled);
        Assert.Contains("off", settings.ReadinessText);

        c.Dialogs.OnConsent = vm => vm.DeclineCommand.Execute(null);
        settings.Enabled = true;
        Assert.False(settings.Enabled);                                    // snapped back
        Assert.False(c.Saved.IdentificationEnabled);

        c.Dialogs.OnConsent = vm => { vm.Contact = "dj@example.com"; vm.AcceptCommand.Execute(null); };
        settings.Enabled = true;
        Assert.True(settings.Enabled);
        Assert.True(c.Saved.IdentificationEnabled);
        Assert.Equal("dj@example.com", settings.Contact);
        Assert.Contains("tags", settings.ReadinessText);

        settings.Enabled = false;                                          // switching it off needs no questions, and consent is remembered
        Assert.False(c.Saved.IdentificationEnabled);
        Assert.NotNull(c.Saved.IdentificationConsentUtc);
        c.Dialogs.OnConsent = vm => throw new InvalidOperationException("must not ask again");
        settings.Enabled = true;
        Assert.True(c.Saved.IdentificationEnabled);
    });

    [Fact]
    public Task SettingsFields_AreSavedAtOnce_AndTheKeyIsNeverShownInATextBox() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.App.Main.SelectedTab = AppTab.Settings;
        await Task.Delay(400);
        var settings = c.App.Main.Settings.Identification;

        settings.Contact = " me@example.com ";
        settings.FpcalcPath = @"C:\Tools\fpcalc.exe";
        settings.SuggestGenres = false;
        var box = Ui.FindDescendant<PasswordBox>(c.App.Window)!;
        box.Password = "MYSECRETKEY";

        var saved = c.Saved;
        Assert.Equal(("me@example.com", @"C:\Tools\fpcalc.exe", false, "MYSECRETKEY"),
            (saved.MusicBrainzContact, saved.FpcalcPath, saved.SuggestGenresFromMusicBrainz, saved.AcoustIdApiKey));
        var texts = Descendants<TextBox>(c.App.Window).Select(t => t.Text).ToList();
        Assert.DoesNotContain(texts, t => t.Contains("MYSECRETKEY"));
        Assert.DoesNotContain(Descendants<TextBlock>(c.App.Window).Select(t => t.Text), t => t.Contains("MYSECRETKEY"));
        Assert.Empty(BindingAudit.FindBroken(c.App.Window));
    });

    [Fact]
    public Task BrowseForFpcalc_FillsThePath_AndClearingTheCacheCallsTheService() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var settings = c.App.Main.Settings.Identification;
        c.Dialogs.FilePicked = @"D:\Tools\fpcalc.exe";

        settings.BrowseFpcalcCommand.Execute(null);
        Assert.Equal(@"D:\Tools\fpcalc.exe", settings.FpcalcPath);
        Assert.Equal(@"D:\Tools\fpcalc.exe", c.Saved.FpcalcPath);

        c.Dialogs.FilePicked = null;                                       // cancelled: nothing changes
        settings.BrowseFpcalcCommand.Execute(null);
        Assert.Equal(@"D:\Tools\fpcalc.exe", settings.FpcalcPath);

        await settings.ClearCacheCommand.ExecuteAsync(null);
        Assert.Equal(1, c.Service.CacheCleared);
    });

    [Fact]
    public Task TheLibrary_OffersIdentify_InTheContextMenu_AndTheDetailsPanel() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var grid = Ui.FindDescendant<DataGrid>(c.App.Window)!;

        var items = grid.ContextMenu!.Items.OfType<MenuItem>().Select(m => m.Header as string).ToList();
        Assert.Contains("Identify online…  (Ctrl+I)", items);

        c.Search.SetSelectedItems([Row(c, "Bravo")]);
        await Task.Delay(300);
        var buttons = Descendants<Button>(c.App.Window).Select(b => b.Content as string).ToList();
        Assert.Contains("Identify online…", buttons);

        c.Search.SetSelectedItems([Row(c, "Bravo"), Row(c, "Charlie")]);
        await Task.Delay(300);
        Assert.Contains("Identify selection online…", Descendants<Button>(c.App.Window).Select(b => b.Content as string));
    });

    [Fact]
    public Task IdentifyWithNothingSelected_DoesNothing() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.Search.SetSelectedItems([]);

        c.App.Main.Library.IdentifyCommand.Execute(null);

        Assert.Null(c.Dialogs.Identify);
        Assert.Empty(c.Dialogs.Infos);
    });

    // ===== covers =====

    [Fact]
    public Task TheChosenReleasesCover_IsTickedByDefaultWhenTheFileHasNone_AndCanBeUnticked() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var path = Path.Combine(c.App.MusicDir, "a.mp3");
        var vm = NewVm(c, [Row(c, "unknown track")]);
        await vm.StartAsync();
        await vm.WhenIdle;
        await vm.WhenIdle;

        Assert.True(vm.CoverEnabled);
        Assert.True(vm.HasCover);
        Assert.NotNull(vm.CoverPicture);
        Assert.Contains("PNG", vm.CoverStatus);
        Assert.True(vm.UseCover);                                        // ticked by default: the file has no cover (it is still written only on Write)
        Assert.False(vm.TrackHasCover);
        Assert.Contains("Cover", vm.ChangeSummary);
        Assert.Equal(["rel-1"], c.Covers.Requests);

        vm.UseCover = false;                                             // the user changes their mind
        Assert.DoesNotContain("Cover", vm.ChangeSummary);
        vm.WriteCommand.Execute(null);
        await Ui.WaitUntilAsync(() => vm.Step == IdentifyStep.Done, what: "written");

        using var f = TagLib.File.Create(path);
        Assert.Empty(f.Tag.Pictures);                                    // text was written, no cover was
    });

    [Fact]
    public Task TheCoverAlone_IsSummarisedBeforeWrite_AndEmbeddedOnWrite() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var path = Path.Combine(c.App.MusicDir, "a.mp3");
        var vm = NewVm(c, [Row(c, "unknown track")]);
        await vm.StartAsync();
        await vm.WhenIdle;
        await vm.WhenIdle;
        foreach (var p in vm.Proposals) p.Selected = false;               // cover only
        Assert.True(vm.UseCover);
        Assert.Equal("Will write: Cover", vm.ChangeSummary);
        using (var untouched = TagLib.File.Create(path)) Assert.Empty(untouched.Tag.Pictures);       // nothing is written before Write

        vm.WriteCommand.Execute(null);
        await Ui.WaitUntilAsync(() => vm.Step == IdentifyStep.Done, what: "written");

        using (var f = TagLib.File.Create(path))
        {
            var picture = Assert.Single(f.Tag.Pictures);
            Assert.Equal(FakeCoverArt.Image().Data, picture.Data.Data);
            Assert.Equal(TagLib.PictureType.FrontCover, picture.Type);
        }
        await Ui.WaitUntilAsync(() => c.Search.Results.Single(r => r.Title == "unknown track").ArtKey == FakeCoverArt.Image().Hash, what: "grid shows the cover");
    });

    [Fact]
    public Task AReleaseWithoutACover_SaysSo_AndOffersNothing() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var vm = NewVm(c, [Row(c, "unknown track")]);
        await vm.StartAsync();
        await vm.WhenIdle;
        await vm.WhenIdle;
        vm.UseCover = true;

        vm.SelectedRelease = vm.Releases.Single(r => r.Release?.ReleaseId == "rel-compilation");
        await vm.WhenIdle;

        Assert.False(vm.HasCover);
        Assert.Null(vm.CoverPicture);
        Assert.False(vm.UseCover);                                       // reset with the release
        Assert.Contains("No cover is on file", vm.CoverStatus);
        vm.SelectedRelease = vm.Releases[0];                             // "No release": nothing to look up
        await vm.WhenIdle;
        Assert.Equal("", vm.CoverStatus);
    });

    [Fact]
    public Task AFileThatHasACover_KeepsIt_UnlessTheUserAsksToReplaceIt() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var path = Path.Combine(c.App.MusicDir, "a.mp3");
        // Give the file a different cover first, through the same safe path.
        var other = FakeCoverArt.Image() with { Data = [.. FakeCoverArt.Image().Data, 0, 0] };
        var seed = await c.App.Service<ITagEditingService>().ApplyAsync([Row(c, "unknown track").Id],
            new TagChangeSet(new Dictionary<TagField, string?>(), other), new TagWriteOptions(), null, default);
        Assert.Equal(1, seed.Written);

        var vm = NewVm(c, [Row(c, "unknown track")]);
        await vm.StartAsync();
        await vm.WhenIdle;
        await vm.WhenIdle;
        Assert.True(vm.TrackHasCover);
        foreach (var p in vm.Proposals) p.Selected = false;
        Assert.False(vm.UseCover);                                           // the file has a cover: not ticked for the user
        vm.UseCover = true;

        vm.WriteCommand.Execute(null);
        await Task.Delay(150);
        Assert.Equal(IdentifyStep.Choose, vm.Step);
        Assert.Contains("Replace the cover the file has", vm.Message);       // told how, not silently skipped
        using (var f = TagLib.File.Create(path)) Assert.Equal(other.Data, f.Tag.Pictures.Single().Data.Data);

        vm.ReplaceCover = true;
        Assert.Equal("Will write: Cover", vm.ChangeSummary);
        vm.WriteCommand.Execute(null);
        await Ui.WaitUntilAsync(() => vm.Step == IdentifyStep.Done, what: "written");

        using var after = TagLib.File.Create(path);
        Assert.Equal(FakeCoverArt.Image().Data, after.Tag.Pictures.Single().Data.Data);
    });

    [Fact]
    public Task WithCoversSwitchedOff_NoCoverIsLookedFor_AndNothingIsShown() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var s = c.Saved;
        s.UseCoverArtArchive = false;
        c.App.Settings.Save(s);
        var vm = NewVm(c, [Row(c, "unknown track")]);

        await vm.StartAsync();
        await vm.WhenIdle;
        await vm.WhenIdle;

        Assert.False(vm.CoverEnabled);
        Assert.Empty(c.Covers.Requests);
        Assert.False(vm.HasCover);
    });

    [Fact]
    public Task TheSettingForCovers_IsSavedAtOnce() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        Assert.True(c.App.Main.Settings.Identification.UseCoverArt);

        c.App.Main.Settings.Identification.UseCoverArt = false;

        Assert.False(c.Saved.UseCoverArtArchive);
    });

    [Fact]
    public Task TheCoverSection_HasNoBrokenBindings() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var vm = NewVm(c, [Row(c, "unknown track")]);
        var window = new IdentifyWindow(vm);
        window.Show();
        try
        {
            await Ui.WaitUntilAsync(() => vm.HasCover, what: "cover loaded");
            await Task.Delay(300);
            Assert.NotNull(Descendants<Image>(window).FirstOrDefault(i => i.Name == "CoverImageBox")?.Source);
            Assert.Empty(BindingAudit.FindBroken(window));
        }
        finally { window.Close(); }
    });

    // ===== many tracks at once =====

    /// <summary>A track whose candidate is a poor audio match, so it must be left for the user to decide.</summary>
    private static void MakeOneDoubtful(Ctx c, string doubtfulTitle)
    {
        var doubtfulId = c.Search.Results.Single(r => r.Title == doubtfulTitle).Id;
        c.Service.Respond = request => new IdentificationResult(request.TrackId, IdentificationMethod.AcoustIdFingerprint, IdentificationOutcome.Found,
            [FakeIdentification.Candidate(score: request.TrackId == doubtfulId ? 0.60 : 0.97)], null, false, DateTime.UtcNow);
    }

    [Fact]
    public Task Many_ClearMatchesAreTicked_DoubtfulOnesAreLeftWithTheirReason_AndOneButtonWritesTheTicked() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        MakeOneDoubtful(c, "Charlie");
        var pathA = Path.Combine(c.App.MusicDir, "a.mp3");
        var pathC = Path.Combine(c.App.MusicDir, "c.mp3");
        var hashC = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(pathC)));
        var vm = NewVm(c, [Row(c, "unknown track"), Row(c, "Bravo"), Row(c, "Charlie")]);

        await vm.StartAsync();

        Assert.Equal(IdentifyStep.Overview, vm.Step);                          // the list, not a track in detail
        Assert.Equal([true, true, false], vm.Queue.Select(q => q.Include));
        Assert.Contains("Needs your decision", vm.Queue[2].Attention);
        Assert.Contains("0.60", vm.Queue[2].Attention);
        Assert.Equal("2 of 3 ready to write · 1 need your decision", vm.OverviewSummary);
        Assert.Equal("Write 2 files", vm.WriteButtonText);
        var first = vm.Queue[0];
        Assert.Equal("Ξανά στη ζωή μου — Χάρις Αλεξίου (AcoustID score 0.97)", first.MatchText);
        Assert.Equal("Το Τραγούδι", first.ReleaseText);
        Assert.Equal("Album artist, Year, Track no., MusicBrainz id, Cover", first.ChangesText);   // exactly what Write does; titles are kept
        Assert.Equal(["rel-1"], c.Covers.Requests.Distinct());

        vm.WriteCommand.Execute(null);
        await Ui.WaitUntilAsync(() => vm.Step == IdentifyStep.Done, what: "written");

        Assert.Equal("2 files written.", vm.ResultSummary);
        using (var f = TagLib.File.Create(pathA))
        {
            Assert.Equal("unknown track", f.Tag.Title);                       // existing values kept
            Assert.Equal((1985u, 5u), (f.Tag.Year, f.Tag.Track));             // empty fields filled
            Assert.Equal(FakeCoverArt.Image().Data, f.Tag.Pictures.Single().Data.Data);   // the cover, ticked by default
        }
        Assert.Equal(hashC, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(pathC))));   // the doubtful one is untouched
        Assert.Equal(["Written", "Written"], vm.Queue.Take(2).Select(q => q.Status));
        Assert.False(vm.Queue[2].Resolved);

        vm.ContinueCommand.Execute(null);
        Assert.Equal(IdentifyStep.Overview, vm.Step);
        Assert.Equal("0 of 3 ready to write · 1 need your decision", vm.OverviewSummary);
    });

    [Fact]
    public Task Many_ADoubtfulTrack_CanBeReviewed_AndTickingInTheDetailAddsItToTheList() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        MakeOneDoubtful(c, "Charlie");
        var vm = NewVm(c, [Row(c, "unknown track"), Row(c, "Charlie")]);
        await vm.StartAsync();
        var doubtful = vm.Queue[1];
        Assert.False(doubtful.Include);
        Assert.True(doubtful.CanInclude);                                      // it has proposals: it can be ticked in the list too

        vm.ReviewCommand.Execute(doubtful);
        await Ui.WaitUntilAsync(() => vm.Step == IdentifyStep.Choose && vm.Proposals.Count > 0, what: "detail");
        await vm.WhenIdle;

        Assert.Same(doubtful, vm.Current);
        Assert.Equal("Το Τραγούδι · 1985 · GR · Album", vm.SelectedRelease!.Text);          // the same release the automatic choice made
        Assert.True(vm.UseCover);
        var year = vm.Proposals.Single(p => p.Field == TagField.Year);
        Assert.True(year.Selected);                                            // empty field: ticked by the rules
        Assert.False(doubtful.Include);                                        // looking is not deciding

        year.Selected = false;                                                 // the user changes a tick ...
        year.Selected = true;                                                  // ... and changes it back: that is a decision
        Assert.True(doubtful.Include);
        Assert.StartsWith("Will write: ", vm.ChangeSummary);

        vm.BackToListCommand.Execute(null);
        Assert.Equal(IdentifyStep.Overview, vm.Step);
        Assert.Equal("Write 2 files", vm.WriteButtonText);
    });

    [Fact]
    public Task Many_ChangesMadeInTheDetail_AreRememberedWhenComingBack() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var vm = NewVm(c, [Row(c, "unknown track"), Row(c, "Bravo")]);
        await vm.StartAsync();

        vm.ReviewCommand.Execute(vm.Queue[0]);
        await Ui.WaitUntilAsync(() => vm.Step == IdentifyStep.Choose && vm.Proposals.Count > 0, what: "detail");
        vm.Proposals.Single(p => p.Field == TagField.Year).Selected = false;
        vm.UseCover = false;
        vm.BackToListCommand.Execute(null);
        Assert.DoesNotContain("Year", vm.Queue[0].ChangesText);
        Assert.DoesNotContain("Cover", vm.Queue[0].ChangesText);

        vm.ReviewCommand.Execute(vm.Queue[1]);
        await Ui.WaitUntilAsync(() => ReferenceEquals(vm.Current, vm.Queue[1]) && vm.Proposals.Count > 0, what: "second track");
        vm.ReviewCommand.Execute(vm.Queue[0]);
        await Ui.WaitUntilAsync(() => ReferenceEquals(vm.Current, vm.Queue[0]) && vm.Proposals.Count > 0, what: "back on the first");

        Assert.False(vm.Proposals.Single(p => p.Field == TagField.Year).Selected);        // as the user left it
        Assert.False(vm.UseCover);
    });

    [Fact]
    public Task Many_TickAllClearMatches_AndUntickAll() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        MakeOneDoubtful(c, "Charlie");
        var vm = NewVm(c, [Row(c, "unknown track"), Row(c, "Bravo"), Row(c, "Charlie")]);
        await vm.StartAsync();

        vm.IncludeNoneCommand.Execute(null);
        Assert.Equal("Write 0 files", vm.WriteButtonText);
        Assert.False(vm.CanWrite);
        vm.IncludeAllReadyCommand.Execute(null);

        Assert.Equal([true, true, false], vm.Queue.Select(q => q.Include));           // the doubtful one is never ticked by "all clear matches"
        Assert.True(vm.CanWrite);
    });

    [Fact]
    public Task Many_WritingWithNothingTicked_SaysSo() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var vm = NewVm(c, [Row(c, "unknown track"), Row(c, "Bravo")]);
        await vm.StartAsync();
        vm.IncludeNoneCommand.Execute(null);

        vm.WriteCommand.Execute(null);
        await Task.Delay(100);

        Assert.Equal(IdentifyStep.Overview, vm.Step);
        Assert.Contains("Tick at least one track", vm.Message);
    });

    [Fact]
    public Task Many_ATrackWithNoMatch_IsInTheListWithItsReason_AndCannotBeTicked() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var noMatchId = Row(c, "Bravo").Id;
        c.Service.Respond = r => r.TrackId == noMatchId
            ? IdentificationResult.Failed(r.TrackId, r.Method, IdentificationOutcome.NoMatch, "No recording matched. Try the other method, or edit the tags by hand.")
            : new IdentificationResult(r.TrackId, IdentificationMethod.AcoustIdFingerprint, IdentificationOutcome.Found, [FakeIdentification.Candidate()], null, false, DateTime.UtcNow);
        var vm = NewVm(c, [Row(c, "unknown track"), Row(c, "Bravo")]);

        await vm.StartAsync();

        var item = vm.Queue[1];
        Assert.Equal("No match", item.Status);
        Assert.Contains("No recording matched", item.Attention);
        Assert.False(item.Include);
        Assert.False(item.CanInclude);
        Assert.Equal("1 of 2 ready to write · 1 with no match or a problem", vm.OverviewSummary);
    });

    [Fact]
    public Task TheCoverIsNotTickedByDefault_WhenTheSettingIsOff() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var s = c.Saved;
        s.EmbedCoverByDefault = false;
        c.App.Settings.Save(s);
        var many = NewVm(c, [Row(c, "unknown track"), Row(c, "Bravo")]);
        var one = NewVm(c, [Row(c, "unknown track")]);

        await many.StartAsync();
        await one.StartAsync();
        await one.WhenIdle;
        await one.WhenIdle;

        Assert.All(many.Queue, q => Assert.DoesNotContain("Cover", q.ChangesText));
        Assert.True(one.HasCover);                                              // still offered
        Assert.False(one.UseCover);                                             // but not ticked
    });

    [Fact]
    public Task AFileThatHasACover_IsNotOfferedAReplacement_InAList() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var other = FakeCoverArt.Image() with { Data = [.. FakeCoverArt.Image().Data, 0, 0] };
        await c.App.Service<ITagEditingService>().ApplyAsync([Row(c, "unknown track").Id],
            new TagChangeSet(new Dictionary<TagField, string?>(), other), new TagWriteOptions(), null, default);
        var vm = NewVm(c, [Row(c, "unknown track"), Row(c, "Bravo")]);

        await vm.StartAsync();

        Assert.DoesNotContain("Cover", vm.Queue[0].ChangesText);                // its cover is kept: replacing is never automatic
        Assert.Contains("Cover", vm.Queue[1].ChangesText);                      // the one without a cover gets one
    });

    [Fact]
    public Task Many_WritingCanBeStopped_AndFailuresAreListedWithoutStoppingTheRest() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var vm = NewVm(c, [Row(c, "unknown track"), Row(c, "Bravo")]);
        await vm.StartAsync();
        var readOnly = Path.Combine(c.App.MusicDir, "b.mp3");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        try
        {
            vm.WriteCommand.Execute(null);
            await Ui.WaitUntilAsync(() => vm.Step == IdentifyStep.Done, what: "done");

            Assert.Equal(1, vm.Queue.Count(q => q.Status == "Written"));
            Assert.True(vm.HasFailures);
            Assert.Contains("Bravo", vm.Failures.Single());
            Assert.Contains("read-only", vm.Failures.Single());
            Assert.Contains("1 file written", vm.ResultSummary);
            Assert.Contains("1 problem", vm.ResultSummary);
        }
        finally { File.SetAttributes(readOnly, FileAttributes.Normal); }
    });

    // ===== genres =====

    private static async Task ClearGenreAsync(Ctx c, string title)
    {
        var summary = await c.App.Service<ITagEditingService>().ApplyAsync([Row(c, title).Id],
            new TagChangeSet(new Dictionary<TagField, string?> { [TagField.Genre] = "" }), new TagWriteOptions(), null, default);
        Assert.Equal(1, summary.Written);
    }

    [Fact]
    public Task AFileWithoutAGenre_GetsTheCommunityGenres_Ticked_AndTheyAreWritten() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        await ClearGenreAsync(c, "unknown track");
        // A search hit has no genres: the window asks for them (from the recording's own record) when it shows the track.
        c.Service.Enrich = cand => cand with { GenreSuggestions = ["classic rock", "art rock", "glam rock", "pop"], GenreLevel = "artist" };
        var path = Path.Combine(c.App.MusicDir, "a.mp3");
        var vm = NewVm(c, [Row(c, "unknown track")]);

        await vm.StartAsync();
        await vm.WhenIdle;
        await vm.WhenIdle;

        var genre = vm.Proposals.Single(p => p.Field == TagField.Genre);
        Assert.Equal("Classic Rock; Art Rock; Glam Rock", genre.Proposed);
        Assert.Equal("MusicBrainz genres of the artist", genre.Source);          // said plainly: these describe the artist
        Assert.True(genre.Selected);                                             // the file has none, so it is filled like any empty field
        Assert.Contains("Genre", vm.ChangeSummary);
        Assert.NotEmpty(c.Service.Enriched);

        vm.WriteCommand.Execute(null);
        await Ui.WaitUntilAsync(() => vm.Step == IdentifyStep.Done, what: "written");

        using var f = TagLib.File.Create(path);
        Assert.Equal(["Classic Rock", "Art Rock", "Glam Rock"], f.Tag.Genres);
    });

    [Fact]
    public Task AGenreTheFileAlreadyHas_IsShownBesideTheProposal_ButNotTicked() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.Service.Enrich = cand => cand with { GenreSuggestions = ["rock"], GenreLevel = "recording" };
        var path = Path.Combine(c.App.MusicDir, "a.mp3");
        var vm = NewVm(c, [Row(c, "unknown track")]);
        await vm.StartAsync();
        await vm.WhenIdle;
        await vm.WhenIdle;

        var genre = vm.Proposals.Single(p => p.Field == TagField.Genre);
        Assert.Equal(("Disco", "Rock"), (genre.Current, genre.Proposed));
        Assert.False(genre.Selected);

        vm.WriteCommand.Execute(null);
        await Ui.WaitUntilAsync(() => vm.Step == IdentifyStep.Done, what: "written");
        using var f = TagLib.File.Create(path);
        Assert.Equal(["Disco"], f.Tag.Genres);                                   // the user's genre is untouched
    });

    [Fact]
    public Task InAList_TheGenreIsPartOfWhatWillChange_ForFilesWithoutOne() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        await ClearGenreAsync(c, "unknown track");
        c.Service.Enrich = cand => cand with { GenreSuggestions = ["disco"], GenreLevel = "recording" };
        var vm = NewVm(c, [Row(c, "unknown track"), Row(c, "Bravo")]);

        await vm.StartAsync();

        Assert.Contains("Genre", vm.Queue[0].ChangesText);                       // no genre in the file: filled
        Assert.DoesNotContain("Genre", vm.Queue[1].ChangesText);                 // Funk is kept
    });

    [Fact]
    public Task TurningGenresOff_RemovesThemFromTheProposals() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        await ClearGenreAsync(c, "unknown track");
        c.Service.Enrich = cand => cand with { GenreSuggestions = ["disco"], GenreLevel = "recording" };
        var s = c.Saved;
        s.SuggestGenresFromMusicBrainz = false;
        c.App.Settings.Save(s);
        var vm = NewVm(c, [Row(c, "unknown track")]);

        await vm.StartAsync();
        await vm.WhenIdle;
        await vm.WhenIdle;

        Assert.DoesNotContain(vm.Proposals, p => p.Field == TagField.Genre);
    });

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
