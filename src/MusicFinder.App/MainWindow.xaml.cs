using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using MusicFinder.App.ViewModels;
using MusicFinder.Domain.Contracts;

namespace MusicFinder.App;

public partial class MainWindow : Window
{
    private const int DwmUseImmersiveDarkMode = 20;
    private readonly ISettingsStore _settingsStore;

    public MainWindow(MainViewModel viewModel, ISettingsStore settingsStore)
    {
        InitializeComponent();
        DataContext = viewModel;
        _settingsStore = settingsStore;

        var settings = settingsStore.Load();
        Width = Math.Clamp(settings.WindowWidth, MinWidth, SystemParameters.VirtualScreenWidth);
        Height = Math.Clamp(settings.WindowHeight, MinHeight, SystemParameters.VirtualScreenHeight);
        if (settings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Dark title bar to match the theme (Windows 10 20H1+ / Windows 11).
        var hwnd = new WindowInteropHelper(this).Handle;
        var enable = 1;
        _ = DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref enable, sizeof(int));
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            var settings = _settingsStore.Load();
            settings.WindowMaximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                settings.WindowWidth = Width;
                settings.WindowHeight = Height;
            }
            _settingsStore.Save(settings);
        }
        catch (IOException) { /* losing window size is harmless */ }
        base.OnClosing(e);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
