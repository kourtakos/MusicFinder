using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MusicFinder.App.Services;

namespace MusicFinder.UiSmokeTests;

/// <summary>Making the whole interface bigger: the shortcuts, the slider in Settings, and that the level is remembered. Always put back to normal.</summary>
public class UiZoomTests
{
    private static ScaleTransform? Scale(Window w) => (w.Content as FrameworkElement)?.LayoutTransform as ScaleTransform;

    [Fact]
    public Task TheShortcuts_ScaleEveryWindow_AndZeroGoesBackToNormal() => Ui.RunAsync(async () =>
    {
        await using var app = await UiTestApp.CreateAsync();
        UiZoom.Initialize(app.Settings);
        app.Window.Show();
        try
        {
            Assert.True(UiZoom.TryHandleKey(Key.OemPlus, ModifierKeys.Control));
            Assert.True(UiZoom.TryHandleWheel(120, ModifierKeys.Control));
            Assert.Equal(1.2, UiZoom.Level);
            Assert.Equal(1.2, Scale(app.Window)!.ScaleX);
            Assert.Equal(1.2, Scale(app.Window)!.ScaleY);
            Assert.Equal(1.2, app.Settings.Load().UiZoom);                 // remembered

            Assert.True(UiZoom.TryHandleKey(Key.OemMinus, ModifierKeys.Control));
            Assert.True(UiZoom.TryHandleWheel(-120, ModifierKeys.Control));
            Assert.Equal(1.0, UiZoom.Level);

            UiZoom.Set(1.7);
            Assert.True(UiZoom.TryHandleKey(Key.D0, ModifierKeys.Control));
            Assert.Equal(1.0, UiZoom.Level);
            Assert.Equal(1.0, app.Settings.Load().UiZoom);
        }
        finally { UiZoom.Set(1.0); }
    });

    [Fact]
    public Task OnlyPlainCtrl_ZoomsOtherCombinationsAreLeftAlone() => Ui.RunAsync(async () =>
    {
        await using var app = await UiTestApp.CreateAsync();
        UiZoom.Initialize(app.Settings);
        try
        {
            Assert.False(UiZoom.TryHandleKey(Key.OemPlus, ModifierKeys.None));
            Assert.False(UiZoom.TryHandleKey(Key.OemPlus, ModifierKeys.Control | ModifierKeys.Alt));
            Assert.False(UiZoom.TryHandleKey(Key.D0, ModifierKeys.Control | ModifierKeys.Shift));
            Assert.False(UiZoom.TryHandleKey(Key.F, ModifierKeys.Control));
            Assert.False(UiZoom.TryHandleWheel(120, ModifierKeys.None));
            Assert.False(UiZoom.TryHandleWheel(0, ModifierKeys.Control));
            Assert.Equal(1.0, UiZoom.Level);
        }
        finally { UiZoom.Set(1.0); }
    });

    [Fact]
    public Task TheLimits_AreRespected() => Ui.RunAsync(async () =>
    {
        await using var app = await UiTestApp.CreateAsync();
        UiZoom.Initialize(app.Settings);
        try
        {
            for (var i = 0; i < 30; i++) UiZoom.Step(1);
            Assert.Equal(UiZoom.MaxLevel, UiZoom.Level);
            for (var i = 0; i < 30; i++) UiZoom.Step(-1);
            Assert.Equal(0.8, UiZoom.Level);
        }
        finally { UiZoom.Set(1.0); }
    });

    [Fact]
    public Task TheSavedLevel_IsAppliedAtStart_AndAnAbsurdOneIsHeldToTheLimits() => Ui.RunAsync(async () =>
    {
        await using var app = await UiTestApp.CreateAsync();
        try
        {
            var s = app.Settings.Load();
            var some = Math.Min(1.5, UiZoom.MaxLevel);                     // this screen may allow less than 150%
            s.UiZoom = some;
            app.Settings.Save(s);
            UiZoom.Initialize(app.Settings);
            Assert.Equal(some, UiZoom.Level);

            s.UiZoom = 40;                                                 // someone edited settings.json by hand
            app.Settings.Save(s);
            UiZoom.Initialize(app.Settings);
            Assert.Equal(UiZoom.MaxLevel, UiZoom.Level);

            s.UiZoom = 0;
            app.Settings.Save(s);
            UiZoom.Initialize(app.Settings);
            Assert.Equal(0.8, UiZoom.Level);
        }
        finally { UiZoom.Set(1.0); }
    });

    [Fact]
    public Task TheSettingsSlider_AndTheShortcuts_StayInStep() => Ui.RunAsync(async () =>
    {
        await using var app = await UiTestApp.CreateAsync();
        UiZoom.Initialize(app.Settings);
        app.Window.Show();
        try
        {
            var settings = app.Main.Settings;
            Assert.Equal(100, settings.UiZoomPercent);

            var start = Math.Max(0.9, Math.Min(1.5, UiZoom.MaxLevel - 0.1));   // always one step below what this screen allows
            settings.UiZoomPercent = (int)Math.Round(start * 100);             // the slider
            Assert.Equal(start, UiZoom.Level);
            Assert.Equal(start, app.Settings.Load().UiZoom);

            UiZoom.Step(1);                                                // a shortcut
            Assert.Equal((int)Math.Round((start + 0.1) * 100), settings.UiZoomPercent);

            settings.ResetZoomCommand.Execute(null);
            Assert.Equal(100, settings.UiZoomPercent);
            Assert.Equal(1.0, UiZoom.Level);

            settings.UiZoomPercent = 500;                                  // out of range is held to the limit
            Assert.Equal(UiZoom.MaxLevel, UiZoom.Level);
        }
        finally { UiZoom.Set(1.0); }
    });

    [Fact]
    public Task TheSettingsPage_WithTheTextSizeSection_HasNoBrokenBinding() => Ui.RunAsync(async () =>
    {
        await using var app = await UiTestApp.CreateAsync();
        UiZoom.Initialize(app.Settings);
        app.Window.Show();
        try
        {
            app.Main.SelectedTab = MusicFinder.App.ViewModels.AppTab.Settings;
            await Task.Delay(400);
            UiZoom.Set(Math.Min(1.5, UiZoom.MaxLevel));
            await Task.Delay(200);

            Assert.Empty(BindingAudit.FindBroken(app.Window));
            Assert.NotNull(Ui.FindDescendant<System.Windows.Controls.Slider>(app.Window));
        }
        finally { UiZoom.Set(1.0); }
    });
}
