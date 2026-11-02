using System.Windows.Controls;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using MusicFinder.App.Services;
using MusicFinder.App.ViewModels;
using MusicFinder.App.Views;
using MusicFinder.Application.Analysis;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;
using MusicFinder.UnitTests.Analysis;

namespace MusicFinder.UiSmokeTests;

/// <summary>Decodes nothing: hands back synthesized music chosen by file name, so tempo and key are known.</summary>
internal sealed class FakeAudioDecoder : IAudioDecoder
{
    public List<string> Decoded { get; } = [];
    public Dictionary<string, Func<float[]>> ByFile { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Failures { get; } = new(StringComparer.OrdinalIgnoreCase);
    public TimeSpan Delay { get; set; }

    public static float[] Music(double bpm, int tonic, bool minor, double seconds = 30)
    {
        var drums = SignalFixtures.Drums(bpm, seconds);
        var chords = SignalFixtures.Chords(tonic, minor, seconds);
        return drums.Zip(chords, (a, b) => a + b).ToArray();
    }

    public async Task<DecodeResult> DecodeAsync(string path, AudioDecodeRequest request, CancellationToken ct)
    {
        var name = Path.GetFileName(path);
        lock (Decoded) Decoded.Add(name);
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        ct.ThrowIfCancellationRequested();
        if (Failures.TryGetValue(name, out var failure)) return DecodeResult.Fail(failure);
        var mono = ByFile.TryGetValue(name, out var make) ? make() : Music(128, 9, true);
        return DecodeResult.Ok(new DecodedAudio(mono, 22050, 200, 44100, 2, "fake"));
    }
}

public class AnalyzeUiTests
{
    private static readonly (string, string, string, string)[] Files =
    [
        ("a.mp3", "Alpha", "Artist One", "Disco"),
        ("b.mp3", "Bravo", "Artist One", "Funk"),
        ("c.mp3", "Charlie", "Artist Two", "Disco"),
    ];

    private sealed record Ctx(UiTestApp App, FakeDialogs Dialogs, FakeAudioDecoder Decoder)
    {
        public SearchViewModel Search => App.Main.Library.Search;
        public AppSettings Saved => App.Settings.Load();
        public string PathOf(string file) => Path.Combine(App.MusicDir, file);
    }

    private static async Task<Ctx> StartAsync()
    {
        var dialogs = new FakeDialogs();
        var decoder = new FakeAudioDecoder();
        var app = await UiTestApp.CreateWithFilesAsync(s =>
        {
            s.AddSingleton<IUserDialogs>(dialogs);
            s.AddSingleton<IAudioDecoder>(decoder);
        }, Files);
        app.Window.Show();
        await app.Main.InitializeAsync();
        await Ui.WaitUntilAsync(() => app.Main.Library.Search.Results.Count == 3, what: "indexed rows");
        var ctx = new Ctx(app, dialogs, decoder);
        await SetLongDurationAsync(ctx);
        return ctx;
    }

    /// <summary>The generated files are a fraction of a second long; the library is told they are real four-minute tracks, so they are worth analysing.</summary>
    private static Task SetLongDurationAsync(Ctx c) =>
        c.App.Service<MusicFinder.Infrastructure.Persistence.SqliteWriteQueue>().EnqueueAsync((conn, tx) => conn.Execute("UPDATE Tracks SET DurationMs = 240000", transaction: tx));

    private static AnalyzeViewModel NewVm(Ctx c, params string[] titles)
    {
        var ids = titles.Select(t => c.Search.Results.Single(r => r.Title == t).Id).ToList();
        return new AnalyzeViewModel(ids, c.App.Service<IAudioAnalysisService>(), c.App.Service<AnalysisRunner>(), c.App.Service<ITagEditingService>(),
            c.App.Service<ITrackRepository>(), c.App.Settings, c.App.Service<MusicFinder.Infrastructure.AppPaths>());
    }

    /// <summary>The user ticking every tempo and key (keys are never ticked for them).</summary>
    private static void TickEverything(AnalyzeViewModel vm)
    {
        vm.TickAllTemposCommand.Execute(null);
        vm.TickAllKeysCommand.Execute(null);
    }

    private static AnalyzeItem Item(AnalyzeViewModel vm, string label) => vm.Items.Single(i => i.Label.EndsWith(label));

    // ===== analysing =====

    [Fact]
    public Task TheTracks_AreAnalysed_AndEachShowsItsEstimate_WithItsConfidence() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.Decoder.ByFile["b.mp3"] = () => FakeAudioDecoder.Music(100, 0, false);
        var vm = NewVm(c, "Alpha", "Bravo");

        await vm.StartAsync();

        var alpha = Item(vm, "Alpha");
        Assert.Equal("Analysed", alpha.Status);
        Assert.Matches(@"^≈12[789] \((high|medium)\)$", alpha.BpmEstimateText);
        Assert.Matches(@"^≈8A \((high|medium)\)$", alpha.KeyEstimateText);          // A minor
        var bravo = Item(vm, "Bravo");
        Assert.Matches(@"^≈(99|100|101) ", bravo.BpmEstimateText);
        Assert.Matches(@"^≈8B ", bravo.KeyEstimateText);                              // C major
        Assert.Equal(("—", "—"), (alpha.BpmNow, alpha.KeyNow));                        // the tags are empty and shown as such
        Assert.False(vm.IsAnalysing);
        Assert.Equal("2 of 2 analysed", vm.ProgressText);
    }
    );

    [Fact]
    public Task AConfidentTempoIsTickedForEmptyTags_AKeyNeverIs_AndNothingIsWrittenYet() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var vm = NewVm(c, "Alpha", "Bravo");
        await vm.StartAsync();

        Assert.All(vm.Items, i => Assert.False(i.WriteKey));                            // keys are the least reliable part: the user decides
        Assert.All(vm.Items, i => Assert.Equal(i.Bpm!.Level == Confidence.High, i.WriteBpm));   // a tempo is ticked only when the analysis was confident
        Assert.All(vm.Items, i => Assert.Contains("less reliable than tempos", i.KeyNote));
        Assert.Equal(vm.Items.Count(i => i.WriteBpm), vm.WriteCount);
        using var f = TagLib.File.Create(c.PathOf("a.mp3"));
        Assert.Equal((0u, null), (f.Tag.BeatsPerMinute, f.Tag.InitialKey));            // an estimate is not a tag until Write
    });

    [Fact]
    public Task Write_PutsTheEstimatesIntoTheFiles_AndTheLibraryAndTheGridFollow() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var vm = NewVm(c, "Alpha");
        await vm.StartAsync();

        TickEverything(vm);

        vm.WriteCommand.Execute(null);
        await Ui.WaitUntilAsync(() => vm.Step == AnalyzeStep.Done, what: "written");

        Assert.Equal("1 file written.", vm.ResultSummary);
        Assert.Equal("Written", Item(vm, "Alpha").Status);
        using (var f = TagLib.File.Create(c.PathOf("a.mp3")))
        {
            Assert.InRange(f.Tag.BeatsPerMinute, 127u, 129u);
            Assert.Equal("8A", f.Tag.InitialKey);
            Assert.Equal(["Disco"], f.Tag.Genres);                                     // nothing else was touched
        }
        await Ui.WaitUntilAsync(() => c.Search.Results.Single(r => r.Title == "Alpha").KeyText == "8A", what: "grid shows the key");
    });

    [Fact]
    public Task AfterWriting_TheAnalysisIsKept_SoAskingAgainDoesNotDecodeAgain() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var vm = NewVm(c, "Alpha");
        await vm.StartAsync();
        TickEverything(vm);
        vm.WriteCommand.Execute(null);
        await Ui.WaitUntilAsync(() => vm.Step == AnalyzeStep.Done, what: "written");
        var decodedBefore = c.Decoder.Decoded.Count;

        var again = NewVm(c, "Alpha");
        await again.StartAsync();

        Assert.Equal(decodedBefore, c.Decoder.Decoded.Count);                          // answered from the remembered analysis
        Assert.Equal("Analysed before", Item(again, "Alpha").Status);
        Assert.Equal(0, again.WriteCount);                                             // and the tags already hold it: nothing to propose
        Assert.Equal("Nothing is ticked to write.", again.WriteSummary);
    });

    [Fact]
    public Task AValueTheFileAlreadyHas_IsKept_UnlessReplacingIsAllowed() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        await c.App.Service<ITagEditingService>().ApplyAsync([c.Search.Results.Single(r => r.Title == "Alpha").Id],
            new TagChangeSet(new Dictionary<TagField, string?> { [TagField.Bpm] = "100", [TagField.Key] = "5B" }), new TagWriteOptions(), null, default);
        await SetLongDurationAsync(c);                                                 // a tag write re-reads the (tiny) file
        var vm = NewVm(c, "Alpha");
        await vm.StartAsync();

        var item = Item(vm, "Alpha");
        Assert.Equal(("100", "5B"), (item.BpmNow, item.KeyNow));
        Assert.False(item.CanWriteBpm);
        Assert.False(item.CanWriteKey);
        Assert.Equal(0, vm.WriteCount);

        vm.AllowOverwrite = true;

        Assert.True(item.CanWriteBpm && item.CanWriteKey);
        Assert.Equal(item.Bpm!.Level == Confidence.High, item.WriteBpm);                // a confident tempo is ticked once replacing is allowed; a key never is
        TickEverything(vm);
        vm.WriteCommand.Execute(null);
        await Ui.WaitUntilAsync(() => vm.Step == AnalyzeStep.Done, what: "written");
        using var f = TagLib.File.Create(c.PathOf("a.mp3"));
        Assert.Equal("8A", f.Tag.InitialKey);
    });

    [Fact]
    public Task HalfAndDouble_ChangeTheTempoThatIsWritten_AndToggleBack() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var vm = NewVm(c, "Alpha");
        await vm.StartAsync();
        var item = Item(vm, "Alpha");
        var estimate = int.Parse(item.Bpm!.Proposed);

        vm.HalfTempoCommand.Execute(item);
        Assert.Equal((estimate / 2.0), double.Parse(item.Bpm!.Proposed), 1);
        Assert.Contains("you chose half", item.BpmNote);

        vm.HalfTempoCommand.Execute(item);                                             // the same button again: back to the estimate
        Assert.Equal(estimate.ToString(), item.Bpm!.Proposed);

        vm.DoubleTempoCommand.Execute(item);
        Assert.Equal(estimate * 2, int.Parse(item.Bpm!.Proposed));
    });

    [Fact]
    public Task TheKeyNotationSetting_DecidesWhatIsWritten() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var s = c.Saved;
        s.AnalysisKeyNotation = KeyNotation.NoteName;
        c.App.Settings.Save(s);
        var vm = NewVm(c, "Alpha");
        await vm.StartAsync();

        Assert.Equal("≈Am (high)", Item(vm, "Alpha").KeyEstimateText.Replace("(medium)", "(high)"));

        TickEverything(vm);

        vm.WriteCommand.Execute(null);
        await Ui.WaitUntilAsync(() => vm.Step == AnalyzeStep.Done, what: "written");
        using var f = TagLib.File.Create(c.PathOf("a.mp3"));
        Assert.Equal("Am", f.Tag.InitialKey);
    });

    [Fact]
    public Task MusicWithNoClearAnswer_IsShownAsNoEstimate_WithTheReason_AndNeverTicked() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.Decoder.ByFile["b.mp3"] = () => SignalFixtures.Silence(30);
        c.Decoder.ByFile["c.mp3"] = () => SignalFixtures.Noise(30);
        var vm = NewVm(c, "Bravo", "Charlie");

        await vm.StartAsync();

        var bravo = Item(vm, "Bravo");
        Assert.Contains("No estimate", bravo.Status);
        Assert.Contains("silent", bravo.Status);
        Assert.Equal("no estimate", bravo.BpmEstimateText);
        Assert.False(bravo.WriteBpm || bravo.WriteKey);
        Assert.Contains("No estimate", Item(vm, "Charlie").Status);
        Assert.Equal(0, vm.WriteCount);
        Assert.False(vm.CanWrite);
    });

    [Fact]
    public Task AFileThatCannotBeRead_SaysSo_WithoutStoppingTheOthers() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.Decoder.Failures["a.mp3"] = "Windows could not decode this .mp3 file (the codec may be missing or the file damaged).";
        var vm = NewVm(c, "Alpha", "Bravo");

        await vm.StartAsync();

        Assert.StartsWith("Cannot be read: Windows could not decode", Item(vm, "Alpha").Status);
        Assert.Equal("Analysed", Item(vm, "Bravo").Status);
        Assert.Equal(1, vm.WriteCount);
    });

    [Fact]
    public Task Stop_EndsTheRun_AndTheTracksNotReachedAreMarked() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.Decoder.Delay = TimeSpan.FromMilliseconds(400);
        var vm = NewVm(c, "Alpha", "Bravo", "Charlie");

        var run = vm.StartAsync();
        await Ui.WaitUntilAsync(() => vm.IsAnalysing && c.Decoder.Decoded.Count >= 1, what: "running");
        vm.StopCommand.Execute(null);
        await run;

        Assert.False(vm.IsAnalysing);
        Assert.Contains(vm.Items, i => i.Status == "Not analysed");
        Assert.True(c.Decoder.Decoded.Count < 3);
    });

    [Fact]
    public Task AnalyzeAgain_ListensAgain_IgnoringTheRememberedAnswer() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var vm = NewVm(c, "Alpha");
        await vm.StartAsync();
        Assert.Single(c.Decoder.Decoded);

        vm.AnalyzeAgainCommand.Execute(null);
        await Ui.WaitUntilAsync(() => c.Decoder.Decoded.Count == 2 && !vm.IsAnalysing, what: "second pass");

        Assert.Equal(2, c.Decoder.Decoded.Count);
        Assert.Equal("Analysed", Item(vm, "Alpha").Status);                           // not "Analysed before"
    });

    [Fact]
    public Task TickAllAndUntickAll_AndSingleTicks_UpdateTheSummary() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var vm = NewVm(c, "Alpha", "Bravo");
        await vm.StartAsync();

        vm.UntickAllCommand.Execute(null);
        Assert.Equal("Nothing is ticked to write.", vm.WriteSummary);
        Assert.False(vm.CanWrite);

        Item(vm, "Alpha").WriteKey = true;
        vm.NotifyTicksChanged();
        Assert.Equal("Will write 1 key to 1 file.", vm.WriteSummary);

        vm.TickAllTemposCommand.Execute(null);
        vm.TickAllKeysCommand.Execute(null);
        Assert.Equal("Will write 2 BPM values and 2 keys to 2 files.", vm.WriteSummary);
    });

    [Fact]
    public Task WritingWithNothingTicked_SaysSo() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var vm = NewVm(c, "Alpha");
        await vm.StartAsync();
        vm.UntickAllCommand.Execute(null);

        vm.WriteCommand.Execute(null);
        await Task.Delay(100);

        Assert.Equal(AnalyzeStep.Review, vm.Step);
        Assert.Contains("Tick at least one", vm.Message);
    });

    [Fact]
    public Task AReadOnlyFile_IsListedAsAProblem_AndLeftUntouched_WhileTheOthersAreWritten() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var vm = NewVm(c, "Alpha", "Bravo");
        await vm.StartAsync();
        File.SetAttributes(c.PathOf("b.mp3"), FileAttributes.ReadOnly);
        try
        {
            TickEverything(vm);
            vm.WriteCommand.Execute(null);
            await Ui.WaitUntilAsync(() => vm.Step == AnalyzeStep.Done, what: "done");

            Assert.True(vm.HasFailures);
            Assert.Contains("Bravo", vm.Failures.Single());
            Assert.Contains("1 file written", vm.ResultSummary);
            using var f = TagLib.File.Create(c.PathOf("b.mp3"));
            Assert.Equal(0u, f.Tag.BeatsPerMinute);
        }
        finally { File.SetAttributes(c.PathOf("b.mp3"), FileAttributes.Normal); }
    });

    // ===== entry points =====

    [Fact]
    public Task TheSelectedTracks_OpenTheAnalysis() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.Search.SetSelectedItems([c.Search.Results.Single(r => r.Title == "Alpha"), c.Search.Results.Single(r => r.Title == "Bravo")]);

        c.App.Main.Library.AnalyzeCommand.Execute(null);

        Assert.NotNull(c.Dialogs.Analyze);
        Assert.Equal("Analyze BPM and key of 2 tracks", c.Dialogs.Analyze!.Heading);
    });

    [Fact]
    public Task WithNothingSelected_NothingOpens() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.Search.SetSelectedItems([]);

        c.App.Main.Library.AnalyzeCommand.Execute(null);

        Assert.Null(c.Dialogs.Analyze);
    });

    [Fact]
    public Task TheMissingAction_OpensTheTracksWithoutABpmOrAKey_AndSaysWhenThereAreNone() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        // Give Alpha both values, Bravo only a BPM: Bravo and Charlie remain.
        await c.App.Service<ITagEditingService>().ApplyAsync([c.Search.Results.Single(r => r.Title == "Alpha").Id],
            new TagChangeSet(new Dictionary<TagField, string?> { [TagField.Bpm] = "120", [TagField.Key] = "8A" }), new TagWriteOptions(), null, default);
        await c.App.Service<ITagEditingService>().ApplyAsync([c.Search.Results.Single(r => r.Title == "Bravo").Id],
            new TagChangeSet(new Dictionary<TagField, string?> { [TagField.Bpm] = "120" }), new TagWriteOptions(), null, default);

        await c.App.Main.Library.AnalyzeMissingCommand.ExecuteAsync(null);

        Assert.Equal("Analyze BPM and key of 2 tracks", c.Dialogs.Analyze!.Heading);

        foreach (var title in new[] { "Bravo", "Charlie" })
            await c.App.Service<ITagEditingService>().ApplyAsync([c.Search.Results.Single(r => r.Title == title).Id],
                new TagChangeSet(new Dictionary<TagField, string?> { [TagField.Bpm] = "120", [TagField.Key] = "8A" }), new TagWriteOptions(), null, default);
        c.Dialogs.Infos.Clear();
        await c.App.Main.Library.AnalyzeMissingCommand.ExecuteAsync(null);

        Assert.Contains("already has a BPM and a key", Assert.Single(c.Dialogs.Infos));
    });

    [Fact]
    public Task TheLibrary_OffersAnalysis_InTheMenu_TheDetailsPanelAndTheLibraryBox() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var grid = Ui.FindDescendant<DataGrid>(c.App.Window)!;

        Assert.Contains("Analyze BPM and key…  (Ctrl+B)", grid.ContextMenu!.Items.OfType<MenuItem>().Select(m => m.Header as string));
        c.Search.SetSelectedItems([c.Search.Results.Single(r => r.Title == "Bravo")]);
        await Task.Delay(300);
        var buttons = Descendants<Button>(c.App.Window).Select(b => b.Content as string).ToList();
        Assert.Contains("Analyze BPM and key…", buttons);
        Assert.Contains("Analyze missing BPM and key…", buttons);
        c.Search.SetSelectedItems([c.Search.Results.Single(r => r.Title == "Bravo"), c.Search.Results.Single(r => r.Title == "Charlie")]);
        await Task.Delay(300);
        Assert.Contains("Analyze BPM and key of selection…", Descendants<Button>(c.App.Window).Select(b => b.Content as string));
    });

    // ===== settings and the window =====

    [Fact]
    public Task TheAnalysisSettings_AreSavedAtOnce_WithinTheirLimits() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var settings = c.App.Main.Settings.Analysis;
        Assert.Equal((KeyNotation.Camelot, 90, 1, false), (settings.SelectedNotation.Notation, settings.Seconds, settings.Concurrency, settings.AllowOverwriteDefault));

        settings.SelectedNotation = AnalysisSettingsViewModel.Notations.Single(n => n.Notation == KeyNotation.OpenKey);
        settings.Seconds = 5;
        settings.Concurrency = 7;
        settings.AllowOverwriteDefault = true;

        var saved = c.Saved;
        Assert.Equal((KeyNotation.OpenKey, 30, 2, true), (saved.AnalysisKeyNotation, saved.AnalysisSeconds, saved.AnalysisConcurrency, saved.AnalysisAllowOverwriteDefault));
        Assert.Equal((30, 2), (settings.Seconds, settings.Concurrency));                // the page shows what was really kept
        Assert.True(NewVm(c, "Alpha").AllowOverwrite);                                  // the window starts as the setting says
    });

    [Fact]
    public Task ForgetEstimates_ClearsTheMemory_AndTheSettingsPageCountsThem() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var vm = NewVm(c, "Alpha", "Bravo");
        await vm.StartAsync();
        var settings = c.App.Main.Settings.Analysis;
        await settings.RefreshCacheTextAsync();
        Assert.Contains("2 tracks", settings.CacheText);

        await settings.ClearEstimatesCommand.ExecuteAsync(null);

        Assert.Equal("No estimates are remembered.", settings.CacheText);
    });

    [Fact]
    public Task TheWindowAndTheSettingsSection_HaveNoBrokenBindings() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var vm = NewVm(c, "Alpha", "Bravo");
        var window = new AnalyzeWindow(vm);
        window.Show();
        try
        {
            await Ui.WaitUntilAsync(() => vm.Items.Count == 2 && vm.Items.All(i => i.Analysis is not null) && !vm.IsAnalysing, what: "analysed");
            await Task.Delay(300);
            Assert.Empty(BindingAudit.FindBroken(window));

            TickEverything(vm);

            vm.WriteCommand.Execute(null);
            await Ui.WaitUntilAsync(() => vm.Step == AnalyzeStep.Done, what: "done");
            await Task.Delay(300);
            Assert.Empty(BindingAudit.FindBroken(window));
        }
        finally { window.Close(); }

        c.App.Main.SelectedTab = AppTab.Settings;
        await Task.Delay(400);
        Assert.Empty(BindingAudit.FindBroken(c.App.Window));
    });

    private static IEnumerable<T> Descendants<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
