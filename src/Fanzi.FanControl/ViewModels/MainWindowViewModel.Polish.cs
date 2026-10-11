using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Fanzi.FanControl.Controls;
using Fanzi.FanControl.Models;
using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.ViewModels;

/// <summary>
/// Polish &amp; feel: the persisted "Reduce motion" switch, the overlay's compact / expanded layout and the
/// main-window keyboard shortcuts (<see cref="KeyboardShortcuts"/>).
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>Reduce motion (persisted as <see cref="AppSettings.ReduceMotion"/>); mirrored into <see cref="MotionPreferences"/>.</summary>
    [ObservableProperty] private bool _reduceMotion;

    /// <summary>Overlay shows gauges only (persisted as <see cref="AppSettings.OverlayCompact"/>); false = gauges + bars + sparkline.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(OverlaySparklineVisible))] private bool _overlayCompact;

    /// <summary>Rows of the help panel's keyboard-shortcut list.</summary>
    public IReadOnlyList<ShortcutHelp> ShortcutHelpEntries => KeyboardShortcuts.HelpEntries;

    partial void OnReduceMotionChanged(bool value)
    {
        MotionPreferences.ReduceMotion = value;
        if (_suppressProfileSync) return;
        _appSettings.ReduceMotion = value;
        _ = SaveSettingsAsync();
    }

    partial void OnOverlayCompactChanged(bool value)
    {
        NotifyOverlayRows();
        OverlayLayoutChanged?.Invoke(this, System.EventArgs.Empty);
        if (_suppressProfileSync) return;
        _appSettings.OverlayCompact = value;
        _ = SaveSettingsAsync();
    }

    /// <summary>
    /// Applies the motion preference from the freshly read settings file before the startup delay ends, so the
    /// loader stops animating within milliseconds for users who asked for less motion. Not saved back.
    /// </summary>
    private void ApplyEarlyMotionPreference(AppSettings loaded)
    {
        _suppressProfileSync = true;
        try { ReduceMotion = loaded.ReduceMotion; }
        finally { _suppressProfileSync = false; }
    }

    /// <summary>
    /// Visibility of each main tab in order (Dashboard, Fans, Sensors, RGB, Tasks, Network, Power, Cleaner,
    /// Alerts, Settings) — the optional modules can be hidden in Settings.
    /// </summary>
    internal IReadOnlyList<bool> TabVisibility => new[]
    {
        true, true, true, true,
        ShowTaskManagerTab, ShowNetworkManagerTab, ShowPowerMonitorTab, ShowSystemCleanerTab,
        true, true,
    };

    /// <summary>Runs a keyboard shortcut; false when it did nothing (e.g. Ctrl+7 with only six tabs visible).</summary>
    public bool ExecuteShortcut(ShortcutCommand command)
    {
        switch (command.Action)
        {
            case ShortcutAction.SwitchTab:
                if (KeyboardShortcuts.TabIndexFor(command.TabNumber, TabVisibility) is not int index) return false;
                SelectedTabIndex = index;
                return true;
            case ShortcutAction.ToggleOverlay:
                ToggleMiniOverlayCommand.Execute(null);
                return true;
            case ShortcutAction.Refresh:
                if (RefreshCommand.CanExecute(null)) RefreshCommand.Execute(null);
                return true;
            case ShortcutAction.Help:
                ToggleHelpCommand.Execute(null);
                return true;
            default:
                return false;
        }
    }
}
