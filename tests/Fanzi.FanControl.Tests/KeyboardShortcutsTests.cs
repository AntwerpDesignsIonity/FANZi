using Avalonia.Input;
using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.Tests;

public sealed class KeyboardShortcutsTests
{
    private static readonly bool[] AllTabsVisible = Enumerable.Repeat(true, 10).ToArray();

    [Theory]
    [InlineData(Key.D1, 1)]
    [InlineData(Key.D5, 5)]
    [InlineData(Key.D9, 9)]
    [InlineData(Key.NumPad3, 3)]
    public void CtrlDigit_SwitchesTabs(Key key, int number)
        => Assert.Equal(new ShortcutCommand(ShortcutAction.SwitchTab, number), KeyboardShortcuts.Resolve(key, KeyModifiers.Control));

    [Fact]
    public void CtrlO_F5_F1_MapToTheirActions()
    {
        Assert.Equal(ShortcutAction.ToggleOverlay, KeyboardShortcuts.Resolve(Key.O, KeyModifiers.Control)?.Action);
        Assert.Equal(ShortcutAction.Refresh, KeyboardShortcuts.Resolve(Key.F5, KeyModifiers.None)?.Action);
        Assert.Equal(ShortcutAction.Help, KeyboardShortcuts.Resolve(Key.F1, KeyModifiers.None)?.Action);
    }

    [Theory]
    [InlineData(Key.D1, KeyModifiers.None)]                            // typing a digit
    [InlineData(Key.D1, KeyModifiers.Control | KeyModifiers.Alt)]      // AltGr chords on some layouts
    [InlineData(Key.D0, KeyModifiers.Control)]
    [InlineData(Key.O, KeyModifiers.None)]
    [InlineData(Key.O, KeyModifiers.Control | KeyModifiers.Shift)]
    [InlineData(Key.F5, KeyModifiers.Control)]
    [InlineData(Key.F1, KeyModifiers.Shift)]
    [InlineData(Key.A, KeyModifiers.Control)]
    public void OtherKeys_AreNotShortcuts(Key key, KeyModifiers modifiers)
        => Assert.Null(KeyboardShortcuts.Resolve(key, modifiers));

    [Theory]
    [InlineData(1, 0)]
    [InlineData(3, 2)]
    [InlineData(8, 7)]
    [InlineData(9, 9)] // last tab (Settings), browser convention
    public void TabIndexFor_AllTabsVisible(int number, int expected)
        => Assert.Equal(expected, KeyboardShortcuts.TabIndexFor(number, AllTabsVisible));

    [Fact]
    public void TabIndexFor_SkipsHiddenModules()
    {
        // Tasks (4) and Network (5) hidden: the 5th visible tab is Power (6).
        var visible = new[] { true, true, true, true, false, false, true, true, true, true };

        Assert.Equal(6, KeyboardShortcuts.TabIndexFor(5, visible));
        Assert.Equal(9, KeyboardShortcuts.TabIndexFor(8, visible));
        Assert.Equal(9, KeyboardShortcuts.TabIndexFor(9, visible));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(7)] // only six visible
    public void TabIndexFor_NoSuchTab_IsNull(int number)
    {
        var sixVisible = new[] { true, true, true, true, false, false, false, false, true, true };
        Assert.Null(KeyboardShortcuts.TabIndexFor(number, sixVisible));
    }

    [Fact]
    public void HelpEntries_ListEveryShortcut()
    {
        string keys = string.Join(" | ", KeyboardShortcuts.HelpEntries.Select(e => e.Keys));

        foreach (var expected in new[] { "Ctrl + 1", "Ctrl + 9", "Ctrl + O", "F5", "F1" })
            Assert.Contains(expected, keys);
        Assert.All(KeyboardShortcuts.HelpEntries, e => Assert.False(string.IsNullOrWhiteSpace(e.Description)));
    }
}
