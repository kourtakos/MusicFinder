using System.Runtime.InteropServices;
using System.Windows.Interop;
using MusicFinder.Application.Hotkeys;
using MusicFinder.App.Localization;

namespace MusicFinder.App.Services.Hotkeys;

/// <summary>
/// <c>RegisterHotKey</c> on a hidden message-only window. Create and use it on the UI thread: WM_HOTKEY is delivered to the
/// thread that registered the shortcut, which is how <see cref="Pressed"/> ends up on the UI thread without any marshaling.
/// </summary>
public sealed class Win32GlobalHotkey : IGlobalHotkey
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;   // holding the keys down must not re-fire
    private const int ErrorHotkeyAlreadyRegistered = 1409;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly System.Windows.Threading.Dispatcher _owner = System.Windows.Threading.Dispatcher.CurrentDispatcher;
    private readonly HwndSource _window;
    private int _currentId;      // 0 = nothing registered
    private int _nextId = 1;

    public event EventHandler? Pressed;
    public HotkeyGesture? Current { get; private set; }

    public Win32GlobalHotkey()
    {
        _window = new HwndSource(new HwndSourceParameters("MusicFinder.HotkeySink") { ParentWindow = HwndMessage, Width = 0, Height = 0 });
        _window.AddHook(WndProc);
    }

    public HotkeyRegistration Register(HotkeyGesture gesture)
    {
        // Alternate between two ids so the new shortcut can be registered before the old one is released.
        var id = _nextId;
        _nextId = _nextId == 1 ? 2 : 1;

        if (!RegisterHotKey(_window.Handle, id, (uint)gesture.Modifiers | ModNoRepeat, (uint)gesture.VirtualKey))
        {
            var code = Marshal.GetLastWin32Error();
            return new HotkeyRegistration(false, code == ErrorHotkeyAlreadyRegistered
                ? Loc.T("{0} is already used by another program (or by Windows). Choose a different shortcut.", gesture)
                : Loc.T("Windows did not accept {0} (error {1}). Choose a different shortcut.", gesture, code));
        }

        if (_currentId != 0) UnregisterHotKey(_window.Handle, _currentId);
        _currentId = id;
        Current = gesture;
        return HotkeyRegistration.Ok;
    }

    public void Unregister()
    {
        if (_currentId != 0) UnregisterHotKey(_window.Handle, _currentId);
        _currentId = 0;
        Current = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _currentId != 0 && wParam.ToInt32() == _currentId)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        // The window belongs to the thread that made it. A container that disposes services after an await may arrive on another thread.
        if (!_owner.CheckAccess())
        {
            _owner.BeginInvoke(Dispose);
            return;
        }
        Unregister();
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
