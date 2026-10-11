using Avalonia.Input;
using System.Collections.Generic;

namespace Fanzi.FanControl.Services;

public enum ShortcutAction { SwitchTab, ToggleOverlay, Refresh, Help }

/// <summary>A resolved shortcut; <see cref="TabNumber"/> is 1..9 for <see cref="ShortcutAction.SwitchTab"/>.</summary>
public readonly record struct ShortcutCommand(ShortcutAction Action, int TabNumber = 0);

/// <summary>One row of the help panel's shortcut list.</summary>
public sealed record ShortcutHelp(string Keys, string Description);

/// <summary>
/// Main-window keyboard shortcuts: Ctrl+1…8 switch to the Nth visible tab, Ctrl+9 jumps to the last one
/// (browser convention, so Settings is always one chord away), Ctrl+O toggles the mini overlay,
/// F5 refreshes, F1 opens the help panel. Pure, so the mapping is unit-tested.
/// </summary>
public static class KeyboardShortcuts
{
    /// <summary>Maps a key press to a shortcut; null when it is not one (modifiers must match exactly).</summary>
    public static ShortcutCommand? Resolve(Key key, KeyModifiers modifiers)
    {
        if (modifiers == KeyModifiers.Control)
        {
            if (key >= Key.D1 && key <= Key.D9) return new ShortcutCommand(ShortcutAction.SwitchTab, key - Key.D0);
            if (key >= Key.NumPad1 && key <= Key.NumPad9) return new ShortcutCommand(ShortcutAction.SwitchTab, key - Key.NumPad0);
            if (key == Key.O) return new ShortcutCommand(ShortcutAction.ToggleOverlay);
            return null;
        }

        if (modifiers == KeyModifiers.None)
        {
            if (key == Key.F5) return new ShortcutCommand(ShortcutAction.Refresh);
            if (key == Key.F1) return new ShortcutCommand(ShortcutAction.Help);
        }

        return null;
    }

    /// <summary>
    /// Tab index for Ctrl+<paramref name="number"/>: the Nth visible tab for 1..8, the last visible tab for 9;
    /// null when there is no such tab. Hidden modules (Settings → Modular sections) are skipped.
    /// </summary>
    public static int? TabIndexFor(int number, IReadOnlyList<bool> tabVisible)
    {
        if (number < 1 || number > 9) return null;

        var visible = new List<int>();
        for (int i = 0; i < tabVisible.Count; i++)
            if (tabVisible[i]) visible.Add(i);
        if (visible.Count == 0) return null;

        if (number == 9) return visible[^1];
        return number <= visible.Count ? visible[number - 1] : null;
    }

    /// <summary>Rows shown in the help panel (F1).</summary>
    public static IReadOnlyList<ShortcutHelp> HelpEntries { get; } = new[]
    {
        new ShortcutHelp("Ctrl + 1 … 8", "Switch to the 1st … 8th tab"),
        new ShortcutHelp("Ctrl + 9", "Jump to the last tab (Settings)"),
        new ShortcutHelp("Ctrl + O", "Show / hide the mini overlay"),
        new ShortcutHelp("F5", "Refresh the sensors now"),
        new ShortcutHelp("F1", "Open / close this help"),
    };
}
