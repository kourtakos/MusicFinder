using System.Runtime.InteropServices;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MusicFinder.App.Services.Dragging;
using MusicFinder.App.Services.Hotkeys;
using MusicFinder.App.ViewModels;
using MusicFinder.Application.Hotkeys;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Enums;

namespace MusicFinder.UiSmokeTests;

/// <summary>
/// The quick-search pop-up, its global shortcut and the shortcut settings. Windows' real RegisterHotKey is exercised separately
/// (it needs the real message loop); everything else runs against fakes so it is deterministic.
/// </summary>
public class QuickSearchTests
{
    private sealed class FakeHotkey : IGlobalHotkey
    {
        public HashSet<string> TakenByOthers { get; } = [];
        public List<string> Registered { get; } = [];
        public HotkeyGesture? Current { get; private set; }
        public event EventHandler? Pressed;

        public HotkeyRegistration Register(HotkeyGesture gesture)
        {
            if (TakenByOthers.Contains(gesture.ToString()))
                return new HotkeyRegistration(false, $"{gesture} is already used by another program (or by Windows). Choose a different shortcut.");
            Current = gesture;
            Registered.Add(gesture.ToString());
            return HotkeyRegistration.Ok;
        }

        public void Unregister() => Current = null;
        public void Press() => Pressed?.Invoke(this, EventArgs.Empty);
        public void Dispose() { }
    }

    private sealed class FakePresenter : IQuickSearchPresenter
    {
        public bool IsShown { get; private set; }
        public int Shows, Hides;
        public void Show() { IsShown = true; Shows++; }
        public void Hide() { IsShown = false; Hides++; }
    }

    private sealed class FakeDragSource : IFileDragSource
    {
        public string Effect { get; set; } = "Copy";
        public List<IReadOnlyList<string>> Drags { get; } = [];
        public DragOutcome StartDrag(IReadOnlyList<string> paths, object source)
        {
            Drags.Add(paths.ToList());
            var now = DateTime.UtcNow;
            return new DragOutcome(Effect, now.AddSeconds(-1), now, "winamp");
        }
    }

    private static readonly (string, string, string, string)[] Files =
    [
        ("a.mp3", "Alpha", "Artist A", "Disco"),
        ("b.mp3", "Bravo", "Artist B", "Funk"),
        ("c.mp3", "Charlie", "Artist A", "Funk"),
    ];

    private sealed record Ctx(UiTestApp App, FakeHotkey Hotkey, FakePresenter Presenter, FakeDragSource Drag, FakeTimeProvider Clock)
    {
        public QuickSearchViewModel Quick => App.Service<QuickSearchViewModel>();
        public QuickSearchService Service => App.Service<QuickSearchService>();
        public SettingsViewModel Settings => App.Main.Settings;
        public ISessionService Session => App.Service<ISessionService>();
    }

    private static async Task<Ctx> StartAsync(Action<FakeHotkey>? setup = null, bool start = true)
    {
        var hotkey = new FakeHotkey();
        setup?.Invoke(hotkey);
        var presenter = new FakePresenter();
        var drag = new FakeDragSource();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var app = await UiTestApp.CreateWithFilesAsync(s =>
        {
            s.AddSingleton<IGlobalHotkey>(hotkey);
            s.AddSingleton<IQuickSearchPresenter>(presenter);   // no real pop-up window: the tests drive the view model
            s.AddSingleton<IFileDragSource>(drag);
            s.AddSingleton<TimeProvider>(clock);
        }, Files);
        app.Window.Show();
        await app.Main.InitializeAsync();
        await Ui.WaitUntilAsync(() => app.Main.Library.Search.Results.Count == 3, what: "indexed rows");
        var ctx = new Ctx(app, hotkey, presenter, drag, clock);
        if (start) ctx.Service.Start();
        return ctx;
    }

    // ===== shortcut registration =====

    [Fact]
    public Task Start_RegistersTheSavedShortcut_WhichDefaultsToCtrlAltSpace() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;

        Assert.Equal(["Ctrl+Alt+Space"], c.Hotkey.Registered);
        Assert.Contains("Active. Press Ctrl+Alt+Space", c.Service.StatusText);
        Assert.False(c.Service.StatusIsError);
    });

    [Fact]
    public Task PressingTheShortcut_TogglesTheWindow() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;

        c.Hotkey.Press();
        Assert.True(c.Presenter.IsShown);
        c.Hotkey.Press();
        Assert.False(c.Presenter.IsShown);
        c.Hotkey.Press();
        Assert.True(c.Presenter.IsShown);
        Assert.Equal((2, 1), (c.Presenter.Shows, c.Presenter.Hides));
    });

    [Fact]
    public Task WhenTheShortcutIsTakenAtStartup_TheAppStillWorks_AndSaysWhy() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync(h => h.TakenByOthers.Add("Ctrl+Alt+Space"));
        await using var _ = c.App;

        Assert.Null(c.Hotkey.Current);
        Assert.True(c.Service.StatusIsError);
        Assert.Contains("already used by another program", c.Service.StatusText);
        Assert.True(c.Settings.HotkeyStatusIsError);                           // visible on the Settings page
    });

    [Fact]
    public Task WhenDisabledInSettings_NothingIsRegistered() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync(start: false);
        await using var _ = c.App;
        var s = c.App.Settings.Load();
        s.QuickSearchEnabled = false;
        c.App.Settings.Save(s);

        c.Service.Start();

        Assert.Empty(c.Hotkey.Registered);
        Assert.Contains("Off.", c.Service.StatusText);
    });

    // ===== choosing a shortcut in Settings =====

    [Fact]
    public Task ChoosingAShortcut_RegistersItFirst_ThenSavesItInCanonicalForm() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;

        Assert.True(c.Settings.TryChangeHotkey("alt+ctrl+k"));

        Assert.Equal("Ctrl+Alt+K", c.Settings.QuickSearchHotkey);
        Assert.Equal("Ctrl+Alt+K", c.Hotkey.Current!.ToString());
        Assert.Equal("Ctrl+Alt+K", c.App.Settings.Load().QuickSearchHotkey);
        Assert.False(c.Settings.HotkeyStatusIsError);
        Assert.Contains("Press Ctrl+Alt+K", c.Settings.HotkeyStatus);
    });

    [Fact]
    public Task AShortcutAnotherProgramOwns_IsRefused_AndTheOldOneKeepsWorking() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync(h => h.TakenByOthers.Add("Ctrl+Alt+K"));
        await using var _ = c.App;

        Assert.False(c.Settings.TryChangeHotkey("Ctrl+Alt+K"));

        Assert.Equal("Ctrl+Alt+Space", c.Settings.QuickSearchHotkey);          // unchanged in the box
        Assert.Equal("Ctrl+Alt+Space", c.Hotkey.Current!.ToString());          // and still the active one
        Assert.Equal("Ctrl+Alt+Space", c.App.Settings.Load().QuickSearchHotkey); // and still saved
        Assert.True(c.Settings.HotkeyStatusIsError);
        Assert.Contains("already used by another program", c.Settings.HotkeyStatus);
    });

    [Theory]
    [InlineData("Space", "Ctrl, Alt or Win")]
    [InlineData("Shift+A", "Ctrl, Alt or Win")]
    [InlineData("Ctrl+Alt+Esc", "Esc closes")]
    [InlineData("Alt+Tab", "belongs to Windows")]
    [InlineData("Ctrl+C", "editing")]
    public Task InvalidShortcuts_AreExplained_AndNothingChanges(string text, string reason) => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var registeredBefore = c.Hotkey.Registered.Count;

        Assert.False(c.Settings.TryChangeHotkey(text));

        Assert.Contains(reason, c.Settings.HotkeyStatus);
        Assert.True(c.Settings.HotkeyStatusIsError);
        Assert.Equal(registeredBefore, c.Hotkey.Registered.Count);              // Windows was not even asked
        Assert.Equal("Ctrl+Alt+Space", c.Settings.QuickSearchHotkey);
    });

    [Fact]
    public Task UseDefault_RestoresCtrlAltSpace() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.Settings.TryChangeHotkey("Ctrl+Alt+K");

        c.Settings.ResetHotkeyCommand.Execute(null);

        Assert.Equal("Ctrl+Alt+Space", c.Settings.QuickSearchHotkey);
        Assert.Equal("Ctrl+Alt+Space", c.Hotkey.Current!.ToString());
    });

    [Fact]
    public Task SwitchingItOff_ReleasesTheShortcut_AndRemembersTheKeys() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.Settings.TryChangeHotkey("Ctrl+Alt+K");

        c.Settings.QuickSearchEnabled = false;

        Assert.Null(c.Hotkey.Current);
        var saved = c.App.Settings.Load();
        Assert.False(saved.QuickSearchEnabled);
        Assert.Equal("Ctrl+Alt+K", saved.QuickSearchHotkey);

        c.Settings.QuickSearchEnabled = true;
        Assert.Equal("Ctrl+Alt+K", c.Hotkey.Current!.ToString());
        Assert.True(c.App.Settings.Load().QuickSearchEnabled);
    });

    [Fact]
    public Task SwitchingItOn_WhenTheKeysAreTaken_LeavesItOff_AndSaysWhy() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.Settings.QuickSearchEnabled = false;
        c.Hotkey.TakenByOthers.Add("Ctrl+Alt+Space");

        c.Settings.QuickSearchEnabled = true;

        Assert.False(c.Settings.QuickSearchEnabled);                            // the box shows the truth
        Assert.False(c.App.Settings.Load().QuickSearchEnabled);
        Assert.True(c.Settings.HotkeyStatusIsError);
    });

    [Theory]
    [InlineData(Key.Space, ModifierKeys.Control | ModifierKeys.Alt, "Ctrl+Alt+Space")]
    [InlineData(Key.K, ModifierKeys.Control, "Ctrl+K")]
    [InlineData(Key.D5, ModifierKeys.Alt, "Alt+5")]
    [InlineData(Key.NumPad7, ModifierKeys.Control, "Ctrl+7")]
    [InlineData(Key.F9, ModifierKeys.Control | ModifierKeys.Shift, "Ctrl+Shift+F9")]
    [InlineData(Key.Prior, ModifierKeys.Control | ModifierKeys.Alt, "Ctrl+Alt+PageUp")]
    [InlineData(Key.Return, ModifierKeys.Windows, "Win+Enter")]
    public void TheShortcutBox_TurnsKeyPressesIntoText(Key key, ModifierKeys mods, string expected)
    {
        var text = HotkeyCapture.ToText(key, Key.None, mods);
        Assert.Equal(expected, text);
        Assert.True(HotkeyGesture.TryParse(text, out _, out var error), error);
    }

    [Fact]
    public void WithAltHeld_WpfReportsKeySystem_AndTheRealKeyIsRecovered() =>
        Assert.Equal("Alt+K", HotkeyCapture.ToText(Key.System, Key.K, ModifierKeys.Alt));

    [Theory]
    [InlineData(Key.LeftCtrl)]
    [InlineData(Key.RightAlt)]
    [InlineData(Key.LeftShift)]
    [InlineData(Key.LWin)]
    public void HoldingOnlyAModifier_ProducesNothingYet(Key modifier) => Assert.Null(HotkeyCapture.ToText(modifier, Key.None, ModifierKeys.Control));

    // ===== the pop-up's search and drag-out =====

    [Fact]
    public Task Typing_SearchesTheWholeLibrary_AndShowsAShortList() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;

        c.Quick.Query = "artist:\"artist a\"";
        await Ui.WaitUntilAsync(() => c.Quick.Results.Count == 2, what: "results");

        Assert.Equal(["Alpha", "Charlie"], c.Quick.Results.Select(r => r.Title));
        Assert.Equal("2 matches", c.Quick.StatusText);

        c.Quick.Query = "";
        await Ui.WaitUntilAsync(() => c.Quick.Results.Count == 0, what: "cleared");
        Assert.Contains("Type to search", c.Quick.StatusText);

        c.Quick.Query = "zzzznothing";
        await Ui.WaitUntilAsync(() => c.Quick.StatusText == "No matches", what: "no matches");
    });

    [Fact]
    public Task TheSearchIgnoresTheFolderSelectionOfTheLibraryView() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        // The library view may be scoped to a folder; the pop-up always searches everything.
        c.App.Main.Library.Search.QueryText = "alpha";
        await Ui.WaitUntilAsync(() => c.App.Main.Library.Search.Results.Count == 1, what: "library filtered");

        c.Quick.Query = "bravo";
        await Ui.WaitUntilAsync(() => c.Quick.Results.Count == 1, what: "popup results");
        Assert.Equal("Bravo", c.Quick.Results[0].Title);
    });

    [Fact]
    public Task Dragging_FromThePopup_OffersTheRealFiles_AndRecordsTheExport() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.Quick.Query = "artist";
        await Ui.WaitUntilAsync(() => c.Quick.Results.Count == 3, what: "results");
        c.Quick.SetSelectedItems([c.Quick.Results[1], c.Quick.Results[0]]);

        var outcome = c.Quick.DragOut(new object());

        Assert.Equal("Copy", outcome!.Effect);
        Assert.Equal([Path.Combine(c.App.MusicDir, "b.mp3"), Path.Combine(c.App.MusicDir, "a.mp3")], Assert.Single(c.Drag.Drags));
        await Ui.WaitUntilAsync(() => c.Session.Active is not null, what: "session started by the drag");
        var entries = await c.Session.GetEntriesAsync(c.Session.Active!.Id, default);
        Assert.Equal(["Alpha", "Bravo"], entries.Select(e => e.Title).Order());
        Assert.All(entries, e => Assert.Equal((SessionPlayStatus.Exported, "winamp"), (e.Status, e.Target)));
    });

    [Fact]
    public Task AfterADrag_ThePopupShowsAPossibleBadge_ButTypingFirstKeepsItExported() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.Quick.Query = "alpha";
        await Ui.WaitUntilAsync(() => c.Quick.Results.Count == 1, what: "results");
        c.Quick.SetSelectedItems([c.Quick.Results[0]]);
        c.Quick.DragOut(new object());
        await Ui.WaitUntilAsync(() => c.Quick.Results[0].SessionBadge == "Exported", what: "exported badge");

        await Task.Run(() => c.Clock.Advance(TimeSpan.FromSeconds(20)));
        await Ui.WaitUntilAsync(() => c.Quick.Results[0].SessionBadge == "Possible", what: "possible badge");

        // A second drag, then typing in the pop-up before the window ends: activity, so it stays exported.
        c.Quick.DragOut(new object());
        for (var i = 0; i < 60 && (await c.Session.GetEntriesAsync(c.Session.Active!.Id, default)).Count < 2; i++)
            await Task.Delay(50);                                             // wait until the export is logged, as it always is within milliseconds
        await Task.Run(() => c.Clock.Advance(TimeSpan.FromSeconds(5)));
        c.Quick.Query = "alph";
        await Ui.WaitUntilAsync(() => c.Quick.Results.Count == 1, what: "results again");
        await Task.Run(() => c.Clock.Advance(TimeSpan.FromSeconds(60)));
        await Task.Delay(150);

        Assert.Equal(1, c.Quick.Results[0].Plays);                              // only the first drag became "possible"
    });

    [Fact]
    public Task APlainEnter_OpensTheSameSearchInTheLibrary_AndPutsThePopupAway() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.Hotkey.Press();
        c.Quick.Query = "bravo";

        c.Quick.OpenInLibraryCommand.Execute(null);

        Assert.False(c.Presenter.IsShown);
        Assert.Equal(AppTab.Library, c.App.Main.SelectedTab);
        Assert.Equal("bravo", c.App.Main.Library.Search.QueryText);
    });

    [Fact]
    public Task Close_HidesThePopup() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.Hotkey.Press();

        c.Quick.CloseCommand.Execute(null);

        Assert.False(c.Presenter.IsShown);
    });

    // ===== the real Windows pieces =====

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    /// <summary>Presses and releases Ctrl+Alt+key with the real keyboard input path (what a user's key press goes through).</summary>
    private static void SendCtrlAlt(byte vk)
    {
        const byte Ctrl = 0x11, Alt = 0x12;
        const uint KeyUp = 0x2;
        keybd_event(Ctrl, 0, 0, UIntPtr.Zero);
        keybd_event(Alt, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, KeyUp, UIntPtr.Zero);
        keybd_event(Alt, 0, KeyUp, UIntPtr.Zero);
        keybd_event(Ctrl, 0, KeyUp, UIntPtr.Zero);
    }

    [Fact]
    public Task RealRegisterHotKey_RefusesAShortcutThatIsAlreadyTaken_AndKeepsTheOldOne() => Ui.RunAsync(async () =>
    {
        await Task.Yield();
        // F23/F24 are real keys that no program uses, so this does not disturb the user's shortcuts.
        using var first = new Win32GlobalHotkey();
        using var second = new Win32GlobalHotkey();
        Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+Shift+F23", out var taken, out _));
        Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+Shift+F24", out var free, out _));

        Assert.True(first.Register(taken).Success);

        var clash = second.Register(taken);
        Assert.False(clash.Success);
        Assert.Contains("already used by another program", clash.Error);
        Assert.Null(second.Current);

        // Changing to a free shortcut releases the old one; a failed change keeps it.
        Assert.True(second.Register(free).Success);
        Assert.False(second.Register(taken).Success);
        Assert.Equal(free, second.Current);
        first.Unregister();
        Assert.True(second.Register(taken).Success);                            // free again once released
    });

    [Fact]
    public Task RealRegisterHotKey_DeliversAKeyPressToPressed() => Ui.RunAsync(async () =>
    {
        using var hotkey = new Win32GlobalHotkey();
        Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+Shift+F22", out var gesture, out _));
        Assert.True(hotkey.Register(gesture).Success);
        var pressed = 0;
        hotkey.Pressed += (_, _) => pressed++;

        // Inject the real keystrokes from another thread so this thread keeps pumping messages.
        await Task.Run(() =>
        {
            const byte Shift = 0x10;
            keybd_event(Shift, 0, 0, UIntPtr.Zero);
            SendCtrlAlt(0x85); // VK_F22
            keybd_event(Shift, 0, 0x2, UIntPtr.Zero);
        });
        await Ui.WaitUntilAsync(() => pressed >= 1, 3000, "WM_HOTKEY");

        Assert.Equal(1, pressed);
    });

    [Fact]
    public Task TheRealWindow_OpensWhereYouLeftIt_KeepsStayingOnTop_AndHasNoBrokenBindings() => Ui.RunAsync(async () =>
    {
        var hotkey = new FakeHotkey();
        var drag = new FakeDragSource();
        var app = await UiTestApp.CreateWithFilesAsync(s =>
        {
            s.AddSingleton<IGlobalHotkey>(hotkey);
            s.AddSingleton<IFileDragSource>(drag);
        }, Files);
        await using var _ = app;
        app.Window.Show();
        await app.Main.InitializeAsync();
        var window = app.Service<MusicFinder.App.Views.QuickSearchWindow>();
        var quick = app.Service<QuickSearchViewModel>();

        window.ShowAndFocus();
        Assert.True(window.IsVisible);
        Assert.True(window.Topmost);
        Assert.False(window.ShowInTaskbar);

        quick.Query = "artist";
        await Ui.WaitUntilAsync(() => quick.Results.Count == 3, what: "results");
        await Task.Delay(300);
        Assert.Empty(BindingAudit.FindBroken(window));

        // It stays when focus moves to another window (a player the track was dropped into).
        app.Window.Activate();
        await Task.Delay(100);
        Assert.True(window.IsVisible);

        // Move it, hide it, open it again: same place, same query.
        window.Left = 123;
        window.Top = 77;
        window.HideAndRemember();
        Assert.False(window.IsVisible);
        var saved = app.Settings.Load();
        Assert.InRange(saved.QuickSearchLeft!.Value, 122, 124);   // device pixels: 123 DIPs becomes 123.33 at 150% scaling
        Assert.InRange(saved.QuickSearchTop!.Value, 76, 78);
        var (left, top) = (window.Left, window.Top);

        window.ShowAndFocus();
        Assert.InRange(window.Left, left - 1, left + 1);
        Assert.InRange(window.Top, top - 1, top + 1);
        Assert.Equal("artist", quick.Query);

        window.CloseForExit();
    });
}
