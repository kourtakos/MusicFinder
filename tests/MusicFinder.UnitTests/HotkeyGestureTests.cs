using MusicFinder.Application.Hotkeys;

namespace MusicFinder.UnitTests;

public class HotkeyGestureTests
{
    private static HotkeyGesture Parse(string text)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var g, out var error), error);
        return g;
    }

    private static string Reject(string? text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out _, out var error));
        Assert.NotEmpty(error);
        return error;
    }

    [Fact]
    public void TheDefault_IsCtrlAltSpace_AndRoundTrips()
    {
        Assert.Equal("Ctrl+Alt+Space", HotkeyGesture.Default.ToString());
        Assert.Equal(HotkeyGesture.Default, Parse(HotkeyGesture.DefaultText));
        Assert.Equal(0x20, HotkeyGesture.Default.VirtualKey);
        Assert.Equal(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, HotkeyGesture.Default.Modifiers);
    }

    [Theory]
    [InlineData("ctrl+alt+space", "Ctrl+Alt+Space")]
    [InlineData("Alt+Ctrl+SPACE", "Ctrl+Alt+Space")]            // modifier order does not matter; text is canonical
    [InlineData(" Control + Shift + k ", "Ctrl+Shift+K")]
    [InlineData("Win+Alt+F9", "Alt+Win+F9")]
    [InlineData("Windows+1", "Win+1")]
    [InlineData("ctrl+alt+Spacebar", "Ctrl+Alt+Space")]
    [InlineData("Ctrl+Alt+pgup", "Ctrl+Alt+PageUp")]
    [InlineData("Ctrl+Alt+f24", "Ctrl+Alt+F24")]
    [InlineData("Alt+Return", "Alt+Enter")]
    public void ParsingIsForgiving_AndTextIsCanonical(string input, string canonical)
    {
        var g = Parse(input);
        Assert.Equal(canonical, g.ToString());
        Assert.Equal(g, Parse(g.ToString()));                       // canonical text parses back to the same shortcut
    }

    [Theory]
    [InlineData("Ctrl+A", 0x41)]
    [InlineData("Ctrl+Z", 0x5A, false)]                              // reserved for editing: rejected below
    [InlineData("Alt+0", 0x30)]
    [InlineData("Ctrl+F1", 0x70)]
    [InlineData("Ctrl+F12", 0x7B)]
    [InlineData("Ctrl+Alt+Tab", 0x09)]
    [InlineData("Ctrl+Alt+Enter", 0x0D)]
    [InlineData("Ctrl+Alt+Left", 0x25)]
    [InlineData("Ctrl+Alt+Down", 0x28)]
    [InlineData("Ctrl+Alt+Home", 0x24)]
    public void VirtualKeyCodes_MatchWindows(string text, int vk, bool valid = true)
    {
        if (!valid) { Reject(text); return; }
        Assert.Equal(vk, Parse(text).VirtualKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NothingTyped_IsRejectedWithAHint(string? text) => Assert.Contains("Press the keys", Reject(text));

    [Theory]
    [InlineData("Space")]
    [InlineData("F5")]
    [InlineData("A")]
    [InlineData("Shift+A")]
    [InlineData("Shift+Space")]
    public void AShortcutWithoutCtrlAltOrWin_WouldBreakTyping_AndIsRejected(string text) => Assert.Contains("Ctrl, Alt or Win", Reject(text));

    [Fact]
    public void ModifiersOnly_NeedAKey() => Assert.Contains("Add a key", Reject("Ctrl+Alt"));

    [Theory]
    [InlineData("Ctrl+Alt+Space+K", "only one")]
    [InlineData("Ctrl+Ctrl+K", "twice")]
    [InlineData("Ctrl++K", "incomplete")]
    [InlineData("Ctrl+Alt+Banana", "not a supported key")]
    [InlineData("Ctrl+Alt+F25", "not a supported key")]
    [InlineData("Ctrl+Alt+Esc", "Esc closes")]
    [InlineData("Ctrl+Alt+;", "not a supported key")]
    public void MalformedShortcuts_SayWhatIsWrong(string text, string expected) =>
        Assert.Contains(expected, Reject(text), StringComparison.OrdinalIgnoreCase);

    [Theory]
    [InlineData("Ctrl+Alt+Delete", "belongs to Windows")]
    [InlineData("Alt+Tab", "belongs to Windows")]
    [InlineData("Alt+F4", "closes windows")]
    [InlineData("Ctrl+C", "editing")]
    [InlineData("ctrl+v", "editing")]
    public void ShortcutsThatWindowsOrEditingOwn_AreRefusedUpFront(string text, string expected) =>
        Assert.Contains(expected, Reject(text));

    [Fact]
    public void SimilarShortcutsAreNotMistakenForReservedOnes()
    {
        Parse("Ctrl+Shift+C");       // not plain Ctrl+C
        Parse("Ctrl+Alt+Tab");      // not Alt+Tab
        Parse("Win+Alt+Delete");
    }

    [Fact]
    public void EqualShortcuts_AreEqual() => Assert.Equal(Parse("alt+ctrl+k"), Parse("Ctrl+Alt+K"));

    [Fact]
    public void ModifierBits_MatchRegisterHotKey()
    {
        // MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8
        Assert.Equal(1, (int)HotkeyModifiers.Alt);
        Assert.Equal(2, (int)HotkeyModifiers.Ctrl);
        Assert.Equal(4, (int)HotkeyModifiers.Shift);
        Assert.Equal(8, (int)HotkeyModifiers.Win);
    }
}

public class WindowPlacementTests
{
    private static readonly ScreenArea Primary = new(0, 0, 1920, 1080);
    private static readonly ScreenArea Right = new(1920, 0, 1920, 1080);

    [Fact]
    public void WithNothingSaved_TheWindowOpensCenteredNearTheTop()
    {
        var (left, top) = WindowPlacement.Resolve(null, null, 600, [Primary], Primary);
        Assert.Equal(660, left);
        Assert.Equal(1080 * 0.14, top, 3);
    }

    [Fact]
    public void ASavedPositionOnAScreen_IsKept()
    {
        Assert.Equal((2500d, 300d), WindowPlacement.Resolve(2500, 300, 600, [Primary, Right], Primary));
    }

    [Fact]
    public void AMonitorThatWasUnplugged_FallsBackToTheDefaultOnTheFallbackScreen()
    {
        var (left, _) = WindowPlacement.Resolve(2500, 300, 600, [Primary], Primary);
        Assert.Equal(660, left);
    }

    [Theory]
    [InlineData(-5000, 100)]
    [InlineData(100, -400)]
    [InlineData(100, 5000)]
    [InlineData(5000, 100)]
    public void APositionFarOffScreen_IsReplaced(double left, double top)
    {
        var (l, t) = WindowPlacement.Resolve(left, top, 600, [Primary], Primary);
        Assert.Equal(660, l);
        Assert.True(t > 0);
    }

    [Fact]
    public void AWindowPartlyOffTheEdge_ButStillGrabbable_IsKept()
    {
        Assert.Equal((-400d, 50d), WindowPlacement.Resolve(-400, 50, 600, [Primary], Primary)); // 200 px of the left corner still visible
    }

    [Fact]
    public void NaNPositions_AreIgnored()
    {
        var (left, _) = WindowPlacement.Resolve(double.NaN, 10, 600, [Primary], Primary);
        Assert.Equal(660, left);
    }
}
