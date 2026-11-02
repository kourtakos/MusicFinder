using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using MusicFinder.App.ViewModels;
using MusicFinder.Application.Hotkeys;
using MusicFinder.Domain.Contracts;

namespace MusicFinder.App.Services.Hotkeys;

/// <summary>What the service needs from the pop-up window (a fake in tests).</summary>
public interface IQuickSearchPresenter
{
    bool IsShown { get; }
    void Show();
    void Hide();
}

/// <summary>
/// Owns the global shortcut and the quick-search pop-up: pressing the shortcut shows the window, pressing it again hides it.
/// Registration problems (the shortcut is taken by another program) never stop the app; they are reported in <see cref="StatusText"/>,
/// which the Settings page shows, and the previously working shortcut keeps working.
/// </summary>
public sealed class QuickSearchService : IDisposable
{
    private readonly IGlobalHotkey _hotkey;
    private readonly ISettingsStore _settings;
    private readonly IServiceProvider _services;
    private IQuickSearchPresenter? _presenter;
    private bool _started;

    public string StatusText { get; private set; } = "";
    public bool StatusIsError { get; private set; }
    public event EventHandler? StatusChanged;

    public QuickSearchService(IGlobalHotkey hotkey, ISettingsStore settings, IServiceProvider services, QuickSearchViewModel viewModel)
    {
        _hotkey = hotkey;
        _settings = settings;
        _services = services;
        _hotkey.Pressed += (_, _) => Toggle();
        viewModel.CloseRequested += (_, _) => Presenter.Hide();
        viewModel.OpenInLibraryRequested += (_, query) => OpenInLibrary(query);
        SetStatus(Describe(settings.Load()), false);
    }

    // The window is created on first use: creating it needs the UI thread and costs nothing until the shortcut is pressed.
    private IQuickSearchPresenter Presenter => _presenter ??= _services.GetRequiredService<IQuickSearchPresenter>();

    /// <summary>Registers the saved shortcut. Call once on the UI thread after start-up.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        var s = _settings.Load();
        if (!s.QuickSearchEnabled) { SetStatus(Describe(s), false); return; }

        if (!HotkeyGesture.TryParse(s.QuickSearchHotkey, out var gesture, out var error))
        {
            SetStatus($"The saved shortcut '{s.QuickSearchHotkey}' is not valid ({error}) so quick search is not available. Choose another in Settings.", true);
            return;
        }
        var result = _hotkey.Register(gesture);
        SetStatus(result.Success ? Describe(s) : $"Quick search is not available: {result.Error}", !result.Success);
    }

    /// <summary>
    /// Applies a change from Settings. The new values are saved only if they work; when registration fails the old shortcut stays active
    /// and nothing is saved. Switching off releases the shortcut but remembers the chosen keys.
    /// </summary>
    public HotkeyRegistration Apply(bool enabled, HotkeyGesture gesture)
    {
        var s = _settings.Load();
        if (!enabled)
        {
            _hotkey.Unregister();
            s.QuickSearchEnabled = false;
            s.QuickSearchHotkey = gesture.ToString();
            _settings.Save(s);
            SetStatus(Describe(s), false);
            return HotkeyRegistration.Ok;
        }

        var result = _hotkey.Register(gesture);
        if (!result.Success)
        {
            SetStatus(result.Error ?? "The shortcut could not be registered.", true);
            return result;
        }

        s.QuickSearchEnabled = true;
        s.QuickSearchHotkey = gesture.ToString();
        _settings.Save(s);
        SetStatus(Describe(s), false);
        return HotkeyRegistration.Ok;
    }

    /// <summary>Shows the window, or hides it if it is already open.</summary>
    public void Toggle()
    {
        if (Presenter.IsShown) Presenter.Hide();
        else Presenter.Show();
    }

    private static string Describe(Domain.Models.AppSettings s) => s.QuickSearchEnabled
        ? $"Active. Press {s.QuickSearchHotkey} in any program to open the quick search; press it again (or Esc) to close it."
        : "Off. The quick search does not open from other programs.";

    private void SetStatus(string text, bool isError)
    {
        StatusText = text;
        StatusIsError = isError;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Brings the main window forward showing the same search, and puts the pop-up away.</summary>
    private void OpenInLibrary(string query)
    {
        Presenter.Hide();
        var main = _services.GetRequiredService<MainViewModel>();
        main.SelectedTab = AppTab.Library;
        main.Library.Search.QueryText = query;

        if (System.Windows.Application.Current?.MainWindow is { } window)
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Show();
            window.Activate();
            window.Topmost = true;   // lifts the window above others; released straight away so it does not stay on top
            window.Topmost = false;
            main.Library.RequestFocusSearch();
        }
    }

    public void Dispose()
    {
        _hotkey.Dispose();
        try { (_presenter as Views.QuickSearchWindow)?.CloseForExit(); }
        catch (InvalidOperationException) { /* the dispatcher is already shutting down */ }
    }
}
