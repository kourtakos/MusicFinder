using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MusicFinder.Application.Display;
using MusicFinder.Domain.Contracts;

namespace MusicFinder.App.Services;

/// <summary>
/// Makes the whole interface bigger or smaller: every window's content is scaled by one shared transform, so text, icons and spacing grow together
/// and the layout is re-flowed to fit (it does not just blur). Ctrl and the mouse wheel, Ctrl and + or -, or Ctrl+0 to go back to normal work in any
/// window; Settings has a slider. The level is remembered.
/// </summary>
public static class UiZoom
{
    private static readonly ScaleTransform SharedTransform = new(1, 1);
    private static ISettingsStore? _store;
    private static bool _hooked;

    public static double Level { get; private set; } = ZoomLevel.Default;
    public static event EventHandler? Changed;

    /// <summary>Loads the saved level and starts listening for the shortcuts in every window. Safe to call more than once.</summary>
    public static void Initialize(ISettingsStore store)
    {
        _store = store;
        Set(store.Load().UiZoom, save: false);
        if (_hooked) return;
        _hooked = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => Apply((Window)s)));
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnWheel));
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnKey));
    }

    /// <summary>The biggest level this screen can show without cutting the layout (the main area of the primary screen).</summary>
    public static double MaxLevel
    {
        get
        {
            var area = SystemParameters.WorkArea;
            return ZoomLevel.MaxForArea(area.Width, area.Height);
        }
    }

    public static void Set(double level, bool save = true)
    {
        var clamped = Math.Clamp(ZoomLevel.Clamp(level), ZoomLevel.Min, Math.Max(MaxLevel, ZoomLevel.Min));
        var changed = Math.Abs(clamped - Level) > 0.0001;
        Level = clamped;
        SharedTransform.ScaleX = clamped;
        SharedTransform.ScaleY = clamped;
        if (save && changed && _store is not null)
        {
            var settings = _store.Load();
            settings.UiZoom = clamped;
            _store.Save(settings);
        }
        if (System.Windows.Application.Current is { } app)
            foreach (Window window in app.Windows) Apply(window);
        if (changed) Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void Step(int direction) => Set(ZoomLevel.Next(Level, direction));

    private static void Apply(Window window)
    {
        if (window.Content is FrameworkElement root && !ReferenceEquals(root.LayoutTransform, SharedTransform)) root.LayoutTransform = SharedTransform;

        // The content needs the same room in its own units, so the smallest the window may be grows with the zoom.
        var min = BaseMinimum.GetValue(window, w => new MinimumSize(w.MinWidth, w.MinHeight));
        window.MinWidth = min.Width * Level;
        window.MinHeight = min.Height * Level;
    }

    /// <summary>What each window's minimum size was before any zoom, so it is always scaled from the original and never compounds.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, MinimumSize> BaseMinimum = new();

    private sealed record MinimumSize(double Width, double Height);

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (TryHandleWheel(e.Delta, Keyboard.Modifiers)) e.Handled = true;
    }

    private static void OnKey(object sender, KeyEventArgs e)
    {
        if (TryHandleKey(e.Key, Keyboard.Modifiers)) e.Handled = true;
    }

    /// <summary>Ctrl and the wheel. Returns true when it was a zoom gesture (and so is used up).</summary>
    public static bool TryHandleWheel(int delta, ModifierKeys modifiers)
    {
        if (modifiers != ModifierKeys.Control || delta == 0) return false;
        Step(delta > 0 ? 1 : -1);
        return true;
    }

    /// <summary>Ctrl with + - or 0. Only Ctrl on its own: Ctrl+Alt+... and Ctrl+Shift+... belong to other things.</summary>
    public static bool TryHandleKey(Key key, ModifierKeys modifiers)
    {
        if (modifiers != ModifierKeys.Control) return false;
        switch (key)
        {
            case Key.OemPlus or Key.Add: Step(1); return true;
            case Key.OemMinus or Key.Subtract: Step(-1); return true;
            case Key.D0 or Key.NumPad0: Set(ZoomLevel.Default); return true;
            default: return false;
        }
    }
}
