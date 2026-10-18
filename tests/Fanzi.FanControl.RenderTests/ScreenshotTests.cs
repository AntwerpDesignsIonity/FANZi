using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fanzi.FanControl.AI;
using Fanzi.FanControl.Controls;
using Fanzi.FanControl.Services;
using Fanzi.FanControl.Services.OpenRgb;
using Fanzi.FanControl.ViewModels;
using Fanzi.FanControl.Views;

namespace Fanzi.FanControl.RenderTests;

/// <summary>
/// Renders the real MainWindow / overlay with fake services (realistic sample data, no hardware,
/// no OpenRGB) to PNGs in .agents/tasks/fanzi-v2.2-polish/screens (override with FANZI_SCREENS_DIR).
/// Run: <c>$env:FANZI_RENDER_TESTS=1; dotnet test tests/Fanzi.FanControl.RenderTests</c>.
/// </summary>
[Trait("Category", "Render")]
public sealed class ScreenshotTests
{
    private const int TabDashboard = 0, TabFans = 1, TabSensors = 2, TabSettings = 10;

    [RenderFact]
    public void MainWindow_NormalState_RendersDashboardAndTabs()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var scope = CreateScope(SampleState.Normal);
        var window = new MainWindow { DataContext = scope.Vm, Width = 1400, Height = 960 };
        window.Show();
        try
        {
            scope.Vm.SelectedTabIndex = TabDashboard;
            Capture(window, "dashboard.png", 1400, 960);
            AssertTabsOnOneRow(window);
            AssertStatusMessageReadableOrHidden(window, expectVisible: true);
            AssertTextNotTrimmed(window, [.. HardwareNameCaptions, "ThermalHistoryTitleText", "RgbChipText", "CoachForecastText"]);
            Assert.Equal("Connected · 3 devices", window.FindControl<TextBlock>("RgbChipText")!.Text);
            AssertHistoryChartsDrawn(window, "CpuHistoryChart", "GpuHistoryChart");
            AssertGaugesNamedForScreenReaders(window);

            // 4.1 thermal frame: four edge strips hug the window, the line is the frame-scale colour of max(CPU, GPU).
            AssertThermalFrame(window, scope.Vm);

            // 4.1 layout: CPU | GPU panels with aligned rows, then history and the full-width cooling bar.
            AssertHardwarePanelsAligned(window);
            ScrollDashboardToEnd(window);
            Capture(window, "dashboard-stats.png", 1400, 960);
            AssertCoolingCardFullWidth(window);
            ScrollDashboardToEnd(window, toTop: true);

            // Hover on the first temperature-reactive card: lift + stronger temperature-tinted glow.
            var tempCard = window.GetVisualDescendants().OfType<Border>()
                .First(b => b.Classes.Contains("temp-card") && b.IsEffectivelyVisible);
            var centre = tempCard.TranslatePoint(new Point(tempCard.Bounds.Width / 2, tempCard.Bounds.Height / 2), window);
            Assert.NotNull(centre);
            window.MouseMove(centre!.Value);
            Capture(window, "dashboard-hover.png", 1400, 960);
            Assert.True(tempCard.IsPointerOver, "temp card should be hovered for dashboard-hover.png");
            window.MouseMove(new Point(2, 2));

            // Same dashboard at the window's MinWidth — checks nothing clips or overlaps when narrow.
            window.Width = window.MinWidth;
            Capture(window, "dashboard-1100.png", 1100, 960);
            AssertHardwarePanelsAligned(window);
            AssertThermalFrame(window, scope.Vm);
            AssertTextNotTrimmed(window, [.. HardwareNameCaptions, "ThermalHistoryTitleText", "ThermalFrameChipText"]);
            AssertTabsOnOneRow(window);
            AssertStatusMessageReadableOrHidden(window, expectVisible: null); // 4.2 HUD: either state is fine at MinWidth, never a bare "…"
            AssertHistoryChartsDrawn(window, "CpuHistoryChart", "GpuHistoryChart");
            window.Width = 1400;

            scope.Vm.SelectedTabIndex = TabFans;
            Dispatcher.UIThread.RunJobs();
            var curveEditor = window.FindControl<FanCurveEditor>("FanCurveEditor")!;
            curveEditor.SelectedIndex = 3; // show the selected-point value chip in the capture
            Capture(window, "fans-curve.png", 1400, 960);
            AssertFanCurveEditorDrawn(window, curveEditor, scope.Vm, expectedPreset: "Balanced");

            // A real mouse drag on the 60 °C point: committed on release, written into the active
            // profile (what the engine reads) and saved through the settings service.
            DragFanCurvePointAndAssertSaved(window, curveEditor, scope, index: 2, toTemp: 66, toDuty: 62);
            window.Width = window.MinWidth;
            Capture(window, "fans-curve-edited-1100.png", 1100, 960);
            AssertFanCurveEditorDrawn(window, curveEditor, scope.Vm, expectedPreset: "Custom");
            window.Width = 1400;

            scope.Vm.SelectedTabIndex = TabSensors;
            Capture(window, "sensors-heatmap.png", 1400, 960);
            AssertHeatmapDrawn(window, scope.Vm);
            window.Width = window.MinWidth;
            Capture(window, "sensors-heatmap-1100.png", 1100, 960);
            AssertHeatmapDrawn(window, scope.Vm);
            window.Width = 1400;

            scope.Vm.SelectedTabIndex = MainWindowViewModel.RgbTabIndex;
            Capture(window, "rgb.png", 1400, 960);
            AssertRgbStudio(window, scope);

            // v4.1.0 Tools tab: real drives / power plans are read the first time the tab opens (read-only queries).
            scope.Vm.SelectedTabIndex = MainWindowViewModel.ToolsTabIndex;
            var toolsClock = System.Diagnostics.Stopwatch.StartNew();
            while (scope.Vm.ToolsVm.Volumes.Count == 0 && toolsClock.Elapsed < TimeSpan.FromSeconds(20))
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(50);
            }
            Assert.NotEmpty(scope.Vm.ToolsVm.Volumes);
            Assert.NotEmpty(scope.Vm.ToolsVm.RepairActions);
            Capture(window, "tools.png", 1400, 960);
            var toolsScroll = window.GetVisualDescendants().OfType<ToolsView>().Single()
                .GetVisualDescendants().OfType<ScrollViewer>().First();
            toolsScroll.Offset = new Vector(0, 900);
            Dispatcher.UIThread.RunJobs();
            Capture(window, "tools-repair-power.png", 1400, 960);
            toolsScroll.Offset = new Vector(0, 0);
            window.Width = window.MinWidth;
            Capture(window, "tools-1100.png", 1100, 960);
            window.Width = 1400;

            scope.Vm.SelectedTabIndex = TabSettings;
            Capture(window, "settings.png", 1400, 960);
            window.FindControl<Border>("OverlayStudioCard")!.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            Capture(window, "settings-overlay-studio.png", 1400, 960);
            Assert.True(window.FindControl<WrapPanel>("OverlayStudioToggles")!.Children.Count >= 10, "overlay studio lists every row");
            // Back to the top of Settings (4.1 adds the Thermal frame row, so the studio scroll moves further down).
            window.FindControl<Border>("OverlayStudioCard")!.FindAncestorOfType<ScrollViewer>()!.Offset = default;
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.FindControl<ToggleSwitch>("ThermalFrameToggle")!.IsChecked, "thermal frame is on by default");
            AssertToastSettingShown(window, scope);
            AssertReduceMotionSettingShown(window, scope);
            window.Width = window.MinWidth;
            Capture(window, "settings-1100.png", 1100, 960);
            AssertTextNotTrimmed(window, ["ToastSettingHelpText", "ReduceMotionHelpText"]);
            window.Width = 1400;

            AssertKeyboardShortcuts(window, scope);
            AssertKeyboardFocusRing(window);
        }
        finally
        {
            window.Close();
        }
    }

    [RenderFact]
    public void MainWindow_HotState_RendersDashboard()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var scope = CreateScope(SampleState.Hot);
        // The first hot poll ran before any window existed, so its alert went to the tray tooltip.
        Assert.Contains("⚠ CPU temperature high", scope.Vm.TrayTooltip);
        var window = new MainWindow { DataContext = scope.Vm, Width = 1400, Height = 960 };
        window.Show();
        try
        {
            Assert.True(scope.Vm.HasCpuTempWarning, "hot sample should raise the CPU temperature warning");
            Assert.True(scope.Vm.IsGameMode, "hot sample runs a game, so the Game Mode tile is populated");
            Capture(window, "dashboard-hot.png", 1400, 960);
            AssertHeroTextNotTrimmed(window);
            AssertThermalFrame(window, scope.Vm);
            Assert.Equal(Fanzi.FanControl.Controls.ThermalState.Critical, scope.Vm.FrameThermalState);

            // Hot state at MinWidth: the Game Mode tile is narrowest here.
            window.Width = window.MinWidth;
            Capture(window, "dashboard-hot-1100.png", 1100, 960);
            AssertHeroTextNotTrimmed(window);
            window.Width = 1400;

            // The next poll over the threshold raises the real temperature toast (the first one, raised
            // before this window existed, went to the tray and is rate-limited — reset that for the render).
            Assert.Null(scope.Vm.TrayAlert); // showing the window cleared the tray alert
            scope.Vm.Alerts.Limiter.Reset();
            scope.Vm.RefreshCommand.Execute(null);
            PumpUntil(() => window.GetVisualDescendants().OfType<Avalonia.Controls.Notifications.NotificationCard>().Any(),
                TimeSpan.FromSeconds(10), "temperature toast");
            Capture(window, "toast-hot.png", 1400, 960);
            AssertToastDrawn(window, "CPU temperature high");
        }
        finally
        {
            window.Close();
        }
    }

    [RenderFact]
    public void Overlay_RendersExpandedAndCompact()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var scope = CreateScope(SampleState.Normal);
        var overlay = new MiniOverlayWindow { DataContext = scope.Vm };
        overlay.Show();
        try
        {
            Assert.False(scope.Vm.OverlayCompact);
            Dispatcher.UIThread.RunJobs();
            // v4: SizeToContent — the window is exactly the card plus the glow margin, every row on.
            int w = (int)overlay.Bounds.Width, h = (int)overlay.Bounds.Height;
            Assert.InRange(h, 260, MiniOverlayWindow.ExpandedHeight);
            Assert.Equal(MiniOverlayWindow.CardWidth + 2 * MiniOverlayWindow.ShadowMargin.Left, w);
            Assert.True(overlay.FindControl<Grid>("OverlayRamRow")!.IsEffectivelyVisible, "RAM row shown by default");
            Assert.True(overlay.FindControl<StackPanel>("OverlayNetworkRow")!.IsEffectivelyVisible, "network rows shown by default");
            Capture(overlay, "overlay-expanded.png", w, h);
            AssertHistoryChartsDrawn(overlay, "CpuSparkline");
            Assert.True(overlay.FindControl<StackPanel>("OverlayBars")!.IsEffectivelyVisible, "expanded overlay shows the bars");
            Assert.True(overlay.FindControl<TextBlock>("OverlayHint")!.IsEffectivelyVisible);

            // Context menu → "Compact (gauges only)": the bars, sparkline and hint go, the window shrinks to the card.
            var menuItem = overlay.FindControl<MenuItem>("CompactModeMenuItem")!;
            Assert.Equal(MenuItemToggleType.CheckBox, menuItem.ToggleType);
            int saves = scope.Settings.SaveCount;
            scope.Vm.OverlayCompact = true;
            Dispatcher.UIThread.RunJobs();
            var menu = overlay.FindControl<Border>("DragRoot")!.ContextMenu!;
            menu.Open(overlay.FindControl<Border>("DragRoot")!); // menu bindings resolve once it is shown
            Dispatcher.UIThread.RunJobs();
            Assert.True(menuItem.IsChecked, "the context menu item is bound to the overlay mode");
            menu.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.True(scope.Settings.SaveCount > saves, "overlay mode change should be saved");
            Assert.True(scope.Settings.LastSaved!.OverlayCompact);

            Dispatcher.UIThread.RunJobs();
            int compactHeight = (int)overlay.Bounds.Height;
            Assert.True(compactHeight < h - 60, $"compact overlay ({compactHeight}px) should be clearly shorter than expanded ({h}px)");
            Capture(overlay, "overlay-compact.png", w, compactHeight);
            Assert.False(overlay.FindControl<StackPanel>("OverlayBars")!.IsEffectivelyVisible, "compact overlay hides the bars");
            Assert.False(overlay.FindControl<ThermalHistoryChart>("CpuSparkline")!.IsEffectivelyVisible, "compact overlay hides the sparkline");
            Assert.False(overlay.FindControl<TextBlock>("OverlayHint")!.IsEffectivelyVisible);
            var gauges = overlay.GetVisualDescendants().OfType<ThermalGauge>().ToList();
            Assert.Equal(2, gauges.Count);
            var root = overlay.FindControl<Border>("DragRoot")!;
            var card = new Rect(root.TranslatePoint(default, overlay)!.Value, root.Bounds.Size);
            Assert.True(card.Bottom <= compactHeight - MiniOverlayWindow.ShadowMargin.Bottom + 0.5, $"card {card} overflows the {compactHeight}px window");
            foreach (var gauge in gauges)
            {
                var rect = new Rect(gauge.TranslatePoint(default, overlay)!.Value, gauge.Bounds.Size);
                Assert.True(gauge.IsEffectivelyVisible && card.Contains(rect), $"gauge {rect} outside the compact card {card}");
            }

            // Back to expanded restores the full layout and size.
            scope.Vm.OverlayCompact = false;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(h, (int)overlay.Bounds.Height);
            Assert.True(overlay.FindControl<ThermalHistoryChart>("CpuSparkline")!.IsVisible);

            // Overlay Studio: hide rows, scale up, recolour — the window follows the content.
            scope.Vm.OverlayShowNetwork = false;
            scope.Vm.OverlayShowRam = false;
            scope.Vm.OverlayShowGpu = false;
            Dispatcher.UIThread.RunJobs();
            int trimmed = (int)overlay.Bounds.Height;
            Assert.True(trimmed < h - 30, $"hiding RAM + network should shrink the overlay ({trimmed} vs {h})");
            Assert.Single(overlay.GetVisualDescendants().OfType<ThermalGauge>(), g => g.IsEffectivelyVisible);
            scope.Vm.OverlayScale = 1.25;
            scope.Vm.OverlayAccent = "Ember";
            Dispatcher.UIThread.RunJobs();
            Assert.InRange(overlay.Bounds.Width, w * 1.2, w * 1.3);
            Capture(overlay, "overlay-custom.png", (int)overlay.Bounds.Width, (int)overlay.Bounds.Height);
            Assert.Equal(1.25, scope.Settings.LastSaved!.OverlayScale);
            Assert.Equal("Ember", scope.Settings.LastSaved!.OverlayAccent);
        }
        finally
        {
            overlay.Close();
        }
    }

    // ── Layout checks ────────────────────────────────────────────────────────

    /// <summary>
    /// v4 RGB studio: colourful effect chips and theme pills, the mood prompt applies a scene, device cards
    /// show every OpenRGB device (numbered duplicates), and the lower sections render (scrolled captures).
    /// </summary>
    private static void AssertRgbStudio(Window window, Scope scope)
    {
        var view = window.GetVisualDescendants().OfType<RgbControlView>().First(v => v.IsEffectivelyVisible);
        var chips = view.FindControl<ItemsControl>("EffectChipList")!;
        Assert.Equal(9, chips.ItemCount);
        Assert.All(chips.GetVisualDescendants().OfType<Button>(), b => Assert.IsAssignableFrom<IGradientBrush>(b.Background));
        var themes = view.FindControl<ItemsControl>("ThemeList")!;
        Assert.All(themes.GetVisualDescendants().OfType<Button>(), b => Assert.IsAssignableFrom<IGradientBrush>(b.Background));

        var rgb = scope.Vm.RgbControl;
        rgb.MoodPrompt = "red galaxy";
        rgb.GenerateMoodCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Models.RgbColor.Red, rgb.PrimaryColor);
        Assert.Contains("galaxy", rgb.AiLightingStatus);

        var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().First();
        scroll.Offset = new Vector(0, 760);
        Dispatcher.UIThread.RunJobs();
        Capture(window, "rgb-colours.png", 1400, 960);
        scroll.Offset = new Vector(0, 1500);
        Dispatcher.UIThread.RunJobs();
        Capture(window, "rgb-devices.png", 1400, 960);
        var cards = view.FindControl<ItemsControl>("DeviceCards")!;
        Assert.Equal(rgb.Devices.Count, cards.ItemCount);
        scroll.Offset = new Vector(0, 0);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>All eleven tab headers (v4.1.0 adds Tools) share one row (no orphaned tab wrapping onto a second line).</summary>
    private static void AssertTabsOnOneRow(Window window)
    {
        var tabs = window.GetVisualDescendants().OfType<TabItem>().Where(t => t.IsEffectivelyVisible).ToList();
        Assert.Equal(11, tabs.Count);
        var rows = tabs.Select(t => Math.Round(t.TranslatePoint(default, window)!.Value.Y)).Distinct().ToList();
        Assert.True(rows.Count == 1, $"tab strip wraps onto {rows.Count} rows at width {window.Width}");
    }

    /// <summary>CPU/GPU model captions under the hero gauges and on the CPU PACKAGE / GPU TEMP cards.</summary>
    private static readonly string[] HardwareNameCaptions =
        { "HeroCpuNameText", "HeroGpuNameText", "CpuPackageNameText", "GpuTempNameText" };

    /// <summary>Game Mode title/detail, the hot-state health insights and the hardware captions render in full.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AssertHeroTextNotTrimmed(Window window)
    {
        AssertTextNotTrimmed(window, ["GameModeTitleText", "GameModeDetailText", .. HardwareNameCaptions]);

        // Health insights: capped at 2 lines (calmer hero at the hot state); the full text is the tooltip.
        var insights = window.FindControl<TextBlock>("HealthInsightsText")!;
        Assert.True(insights.IsEffectivelyVisible, "health insights should be visible");
        Assert.Equal(2, insights.MaxLines);
        Assert.InRange(insights.TextLayout.TextLines.Count, 1, 2);
        Assert.Equal(insights.Text, ToolTip.GetTip(insights) as string);
    }

    /// <summary>Each named TextBlock is visible and laid out in full (no trailing ellipsis, no dropped lines).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AssertTextNotTrimmed(Window window, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            var text = window.FindControl<TextBlock>(name)!;
            Assert.True(text.IsEffectivelyVisible, $"{name} should be visible");
            Assert.False(string.IsNullOrEmpty(text.Text), $"{name} should have text");
            var lines = text.TextLayout.TextLines;
            bool trimmed = lines.Any(l => l.HasCollapsed);
            // MaxLines drops overflow lines without collapsing them, so also check every character is laid out.
            bool linesDropped = lines.Sum(l => l.Length) < text.Text!.Length;
            Assert.False(trimmed || linesDropped, $"{name} ('{text.Text}') is trimmed at width {window.Width}");
        }
    }

    /// <summary>Each named history chart is visible, has data in its window and keeps its "now" dot inside its bounds.</summary>
    private static void AssertHistoryChartsDrawn(TopLevel topLevel, params string[] names)
    {
        foreach (var name in names)
        {
            var chart = topLevel.FindControl<Fanzi.FanControl.Controls.ThermalHistoryChart>(name)!;
            Assert.True(chart.IsEffectivelyVisible, $"{name} should be visible");
            Assert.True(chart.Stats is not null, $"{name} has no samples in its window");
            var now = chart.NowPoint;
            Assert.True(now is { } p && p.X > 0 && p.X <= chart.Bounds.Width && p.Y >= 0 && p.Y <= chart.Bounds.Height,
                $"{name} 'now' dot {now} lies outside {chart.Bounds.Size}");
        }
    }

    /// <summary>The curve editor is on screen with every point and the live marker inside its plot, and its captions render in full.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AssertFanCurveEditorDrawn(Window window, FanCurveEditor editor, MainWindowViewModel vm, string expectedPreset)
    {
        Assert.True(editor.IsEffectivelyVisible, "fan curve editor should be visible on the Fans tab");
        var origin = editor.TranslatePoint(default, window)!.Value;
        Assert.True(origin.Y >= 0 && origin.Y + editor.Bounds.Height <= window.Height,
            $"fan curve editor ({origin}, {editor.Bounds.Size}) is not fully on screen");

        var plot = FanCurveEditor.PlotRect(editor.Bounds.Size);
        var positions = editor.PointPositions;
        Assert.Equal(vm.FanCurvePoints.Count, positions.Length);
        Assert.All(positions, p => Assert.True(plot.Inflate(0.5).Contains(p), $"curve point {p} outside plot {plot}"));

        var marker = editor.MarkerPoint;
        Assert.True(marker is { } m && plot.Inflate(0.5).Contains(m), $"live marker {marker} outside plot {plot}");
        Assert.Equal(FanCurveMath.Interpolate(vm.FanCurvePoints, vm.HottestTempValue!.Value), editor.MarkerDuty!.Value, 9);

        Assert.Equal(expectedPreset, vm.FanCurvePresetLabel);
        AssertTextNotTrimmed(window, ["FanCurvePresetText", "FanCurveStatusText", "FanCurveLiveText", "FanCurveHelpText"]);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void DragFanCurvePointAndAssertSaved(Window window, FanCurveEditor editor, Scope scope, int index, double toTemp, double toDuty)
    {
        var plot = FanCurveEditor.PlotRect(editor.Bounds.Size);
        Point ToWindow(CurvePoint p) => editor.TranslatePoint(FanCurveEditor.ToPixel(p, plot), window)!.Value;

        int savesBefore = scope.Settings.SaveCount;
        var from = ToWindow(scope.Vm.FanCurvePoints[index]);
        var to = ToWindow(new CurvePoint(toTemp, toDuty));
        window.MouseDown(from, Avalonia.Input.MouseButton.Left);
        window.MouseMove(new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2));
        window.MouseMove(to);
        window.MouseUp(to, Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new CurvePoint(toTemp, toDuty), scope.Vm.FanCurvePoints[index]);
        Assert.True(scope.Vm.FanCurveInUse);
        Assert.True(scope.Settings.SaveCount > savesBefore, "curve edit should be saved");
        var saved = scope.Settings.LastSaved!;
        var active = saved.Profiles.Single(p => p.Id == saved.ActiveProfileId);
        Assert.Contains(active.FanCurve, p => p.TemperatureC == toTemp && p.FanPercent == toDuty);
        Assert.Equal(scope.Vm.FanCurvePoints.Count, active.FanCurve.Count);
    }

    /// <summary>
    /// Sensors heatmap: one tile per core (8) coloured by its temperature, CPU summary and GPU tiles, every tile
    /// fully inside the heatmap card and on screen, and no tile text cut off.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AssertHeatmapDrawn(Window window, MainWindowViewModel vm)
    {
        Assert.Equal(8, vm.CpuCoreTiles.Count);
        Assert.True(vm.CpuSummaryTiles.Count >= 3, "CPU summary tiles missing");
        Assert.True(vm.GpuTiles.Count >= 4, "GPU tiles missing");
        Assert.All(vm.CpuCoreTiles, t => Assert.Contains("load", t.Detail));

        var card = window.FindControl<Border>("SensorHeatmapCard")!;
        Assert.True(card.IsEffectivelyVisible, "heatmap card should be visible on the Sensors tab");
        var cardRect = new Rect(card.TranslatePoint(default, window)!.Value, card.Bounds.Size);
        Assert.True(cardRect.Bottom <= window.Height, $"heatmap card {cardRect} runs off the {window.Height}px window");

        var tiles = card.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("heat-tile")).ToList();
        Assert.Equal(vm.CpuCoreTiles.Count + vm.CpuSummaryTiles.Count + vm.GpuTiles.Count, tiles.Count);
        foreach (var tile in tiles)
        {
            var rect = new Rect(tile.TranslatePoint(default, window)!.Value, tile.Bounds.Size);
            Assert.True(cardRect.Contains(rect), $"heat tile {rect} outside the card {cardRect}");
            var vmTile = Assert.IsType<SensorTileViewModel>(tile.DataContext);
            var accent = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(vmTile.AccentBrush).Color;
            Assert.Equal(vmTile.Color, accent);
            foreach (var text in tile.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible))
            {
                bool trimmed = text.TextLayout.TextLines.Any(l => l.HasCollapsed);
                Assert.False(trimmed, $"heat tile text '{text.Text}' is trimmed at width {window.Width}");
            }
        }

        // Hotter core ⇒ warmer tile: the hottest core's colour is the scale colour of its temperature.
        var hottest = vm.CpuCoreTiles.OrderByDescending(t => t.FillPercent).First();
        Assert.NotEqual(vm.CpuCoreTiles.OrderBy(t => t.FillPercent).First().Color, hottest.Color);
        AssertTextNotTrimmed(window, ["CpuHeatmapCaptionText"]);
    }

    /// <summary>The persisted "Toast notifications" toggle is on screen in the Settings capture and bound to the VM.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AssertToastSettingShown(Window window, Scope scope)
    {
        var toggle = window.FindControl<ToggleSwitch>("ToastNotificationsToggle")!;
        Assert.True(toggle.IsEffectivelyVisible, "toast toggle should be visible on the Settings tab");
        // v4.1.0: Settings grew (Tools / Thermal Frame toggles) — scroll the toggle into view like a user would.
        toggle.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        var origin = toggle.TranslatePoint(default, window)!.Value;
        Assert.True(origin.Y >= 0 && origin.Y + toggle.Bounds.Height <= window.Height, $"toast toggle at {origin} is off screen");
        Assert.Equal(scope.Vm.ToastNotificationsEnabled, toggle.IsChecked);
        AssertTextNotTrimmed(window, ["ToastSettingHelpText"]);

        // Flipping it persists through the settings service (then restore).
        int saves = scope.Settings.SaveCount;
        scope.Vm.ToastNotificationsEnabled = false;
        Assert.True(scope.Settings.SaveCount > saves, "toast setting change should be saved");
        Assert.False(scope.Settings.LastSaved!.ToastNotificationsEnabled);
        scope.Vm.ToastNotificationsEnabled = true;
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The persisted "Reduce motion" toggle is on screen, bound, saved, and switches the window into reduced motion.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AssertReduceMotionSettingShown(Window window, Scope scope)
    {
        var toggle = window.FindControl<ToggleSwitch>("ReduceMotionToggle")!;
        Assert.True(toggle.IsEffectivelyVisible, "reduce-motion toggle should be visible on the Settings tab");
        toggle.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        var origin = toggle.TranslatePoint(default, window)!.Value;
        Assert.True(origin.Y >= 0 && origin.Y + toggle.Bounds.Height <= window.Height, $"reduce-motion toggle at {origin} is off screen");
        Assert.Equal(scope.Vm.ReduceMotion, toggle.IsChecked);
        AssertTextNotTrimmed(window, ["ReduceMotionHelpText"]);

        int saves = scope.Settings.SaveCount;
        scope.Vm.ReduceMotion = true;
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.True(scope.Settings.SaveCount > saves, "reduce-motion change should be saved");
            Assert.True(scope.Settings.LastSaved!.ReduceMotion);
            Assert.True(MotionPreferences.ReduceMotion);
            Assert.Contains("reduce-motion", window.Classes);
            Assert.True(toggle.IsChecked);
        }
        finally
        {
            scope.Vm.ReduceMotion = false;
            Dispatcher.UIThread.RunJobs();
        }
        Assert.False(MotionPreferences.ReduceMotion);
        Assert.DoesNotContain("reduce-motion", window.Classes);
    }

    private static void Press(Window window, Avalonia.Input.Key key, Avalonia.Input.RawInputModifiers modifiers = Avalonia.Input.RawInputModifiers.None)
    {
        var physical = key switch
        {
            Avalonia.Input.Key.F1 => Avalonia.Input.PhysicalKey.F1,
            Avalonia.Input.Key.F5 => Avalonia.Input.PhysicalKey.F5,
            Avalonia.Input.Key.O => Avalonia.Input.PhysicalKey.O,
            >= Avalonia.Input.Key.D0 and <= Avalonia.Input.Key.D9 => Avalonia.Input.PhysicalKey.Digit0 + (key - Avalonia.Input.Key.D0),
            _ => Avalonia.Input.PhysicalKey.None,
        };
        window.KeyPress(key, modifiers, physical, null);
        window.KeyRelease(key, modifiers, physical, null);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Real key presses: F1 opens the help panel (captured with its shortcut list), Ctrl+N switches tabs,
    /// Ctrl+O toggles the overlay and F5 refreshes.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AssertKeyboardShortcuts(Window window, Scope scope)
    {
        var vm = scope.Vm;
        const Avalonia.Input.RawInputModifiers ctrl = Avalonia.Input.RawInputModifiers.Control;
        vm.SelectedTabIndex = TabDashboard;
        Dispatcher.UIThread.RunJobs();

        window.MouseMove(new Point(2, 2)); // no stray hover tooltip in the capture
        Press(window, Avalonia.Input.Key.F1);
        Assert.True(vm.ShowHelp, "F1 should open the help panel");
        Capture(window, "help-shortcuts.png", 1400, 960);
        var panel = window.FindControl<Border>("HelpPanel")!;
        var panelRect = new Rect(panel.TranslatePoint(default, window)!.Value, panel.Bounds.Size);
        Assert.True(panel.IsEffectivelyVisible && new Rect(0, 0, window.Width, window.Height).Contains(panelRect), $"help panel {panelRect} not fully on screen");
        var list = window.FindControl<ItemsControl>("HelpShortcutsList")!;
        Assert.Equal(KeyboardShortcuts.HelpEntries.Count, list.ItemCount);
        var caps = list.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("kbd")).ToList();
        Assert.Equal(KeyboardShortcuts.HelpEntries.Count, caps.Count);
        foreach (var text in list.GetVisualDescendants().OfType<TextBlock>())
        {
            Assert.True(text.IsEffectivelyVisible, $"shortcut text '{text.Text}' hidden");
            Assert.False(text.TextLayout.TextLines.Any(l => l.HasCollapsed), $"shortcut text '{text.Text}' is trimmed");
        }
        foreach (var cap in caps)
        {
            var label = cap.GetVisualDescendants().OfType<TextBlock>().Single();
            Assert.True(label.Bounds.Width <= cap.Bounds.Width, $"key cap '{label.Text}' overflows its {cap.Bounds.Width}px cap");
        }
        Press(window, Avalonia.Input.Key.F1);
        Assert.False(vm.ShowHelp, "F1 again closes the help panel");

        Press(window, Avalonia.Input.Key.D3, ctrl);
        Assert.Equal(TabSensors, vm.SelectedTabIndex);
        Press(window, Avalonia.Input.Key.D9, ctrl);
        Assert.Equal(TabSettings, vm.SelectedTabIndex); // Ctrl+9 = last tab
        Press(window, Avalonia.Input.Key.D1, ctrl);
        Assert.Equal(TabDashboard, vm.SelectedTabIndex);

        Press(window, Avalonia.Input.Key.O, ctrl);
        Assert.True(vm.MiniOverlayVisible, "Ctrl+O should show the overlay");
        Press(window, Avalonia.Input.Key.O, ctrl);
        Assert.False(vm.MiniOverlayVisible, "Ctrl+O again hides it");

        // F5 runs a poll through the same RefreshCommand as the Refresh button.
        int historyBefore = vm.HistoryRevision;
        Press(window, Avalonia.Input.Key.F5);
        PumpUntil(() => vm.HistoryRevision > historyBefore && !vm.IsRefreshing, TimeSpan.FromSeconds(10), "F5 refresh");
        Assert.StartsWith("Updated", vm.LastUpdated, StringComparison.Ordinal);
    }

    /// <summary>Keyboard focus shows the theme's accent ring (captured on the Settings tab's Reduce-motion toggle).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AssertKeyboardFocusRing(Window window)
    {
        var ring = Application.Current!.FindResource("FanziFocusRing");
        Assert.NotNull(ring);
        foreach (var control in window.GetVisualDescendants().OfType<Control>()
                     .Where(c => c.TemplatedParent is null // template parts (scrollbar / spinner repeat buttons) aren't tab stops
                                 && c.GetType() is var t
                                 && (t == typeof(Button) || t == typeof(ToggleSwitch) || t == typeof(Slider) || t == typeof(TabItem) || t == typeof(RadioButton))))
            Assert.True(ReferenceEquals(ring, control.FocusAdorner), $"{control.GetType().Name} '{control.Name}' lacks the focus ring");

        var vm = (MainWindowViewModel)window.DataContext!;
        vm.SelectedTabIndex = TabSettings;
        Dispatcher.UIThread.RunJobs();
        var toggle = window.FindControl<ToggleSwitch>("ReduceMotionToggle")!;
        Assert.True(toggle.Focus(Avalonia.Input.NavigationMethod.Tab), "toggle should take keyboard focus");
        Capture(window, "focus-ring.png", 1400, 960);

        var adorner = window.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.Margin.Left < 0 && b.BorderBrush is Avalonia.Media.ISolidColorBrush s && s.Color == Avalonia.Media.Color.Parse("#60A5FA")
                                 && b.IsEffectivelyVisible);
        Assert.NotNull(adorner);
        // The adorner layer positions the ring over the focused control (it is not a visual child of it).
        var adorned = Avalonia.Controls.Primitives.AdornerLayer.GetAdornedElement(adorner!);
        Assert.True(ReferenceEquals(adorned, toggle) || adorned?.FindAncestorOfType<ToggleSwitch>() == toggle,
            $"focus ring adorns {adorned?.GetType().Name ?? "nothing"} instead of the focused toggle");
        Assert.False(Avalonia.Controls.Primitives.AdornerLayer.GetIsClipEnabled(adorner!), "the ring is drawn outside the control, so it must not be clipped");
    }

    /// <summary>Every dashboard gauge exposes its reading to screen readers.</summary>
    private static void AssertGaugesNamedForScreenReaders(Window window)
    {
        var names = window.GetVisualDescendants().OfType<ThermalGauge>().Where(g => g.IsEffectivelyVisible)
            .Select(g => Avalonia.Automation.AutomationProperties.GetName(g)).ToList();
        Assert.Contains("IO-NITY HEALTH 88/100", names);
        Assert.Contains("CPU 72 °C", names);
        Assert.Contains("GPU 64 °C", names);
    }

    private static void ScrollDashboardToEnd(Window window, bool toTop = false)
    {
        var scroller = window.FindControl<Border>("ThermalHistoryPanel")!.FindAncestorOfType<ScrollViewer>()!;
        scroller.Offset = toTop ? default : new Vector(0, Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height));
        Dispatcher.UIThread.RunJobs();
    }

    private static Rect RectIn(Visual visual, Visual root) => new(visual.TranslatePoint(default, root)!.Value, visual.Bounds.Size);

    /// <summary>
    /// 4.1 layout: the CPU and GPU panels sit side by side with the same top and height; their stat grids start at the
    /// same y (shared row sizes), every stat tile in a panel has one height and each panel holds 4 tiles.
    /// </summary>
    private static void AssertHardwarePanelsAligned(Window window)
    {
        var cpu = RectIn(window.FindControl<Border>("CpuPanel")!, window);
        var gpu = RectIn(window.FindControl<Border>("GpuPanel")!, window);
        Assert.Equal(cpu.Top, gpu.Top, 1);
        Assert.Equal(cpu.Height, gpu.Height, 1);
        Assert.True(cpu.Right < gpu.Left, $"CPU panel {cpu} overlaps GPU panel {gpu}");
        Assert.Equal(cpu.Width, gpu.Width, 1);

        var cpuStats = window.FindControl<Grid>("CpuStatCards")!;
        var gpuStats = window.FindControl<Grid>("GpuStatCards")!;
        Assert.Equal(RectIn(cpuStats, window).Top, RectIn(gpuStats, window).Top, 1);
        foreach (var stats in new[] { cpuStats, gpuStats })
        {
            var tiles = stats.Children.OfType<Border>().ToList();
            Assert.Equal(4, tiles.Count);
            Assert.All(tiles, t => Assert.Contains("stat-tile", t.Classes));
            Assert.Single(tiles.Select(t => Math.Round(t.Bounds.Height)).Distinct()); // equal heights
            foreach (var text in stats.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible))
                Assert.False(text.TextLayout.TextLines.Any(l => l.HasCollapsed), $"stat text '{text.Text}' is trimmed at width {window.Width}");
        }

        // Temperature cards of one panel share a height, and both panels' temperature rows match.
        var temps = window.FindControl<Border>("CpuPanel")!.GetVisualDescendants().OfType<Border>()
            .Concat(window.FindControl<Border>("GpuPanel")!.GetVisualDescendants().OfType<Border>())
            .Where(b => b.Classes.Contains("temp-card")).ToList();
        Assert.Equal(5, temps.Count);
        Assert.Single(temps.Select(t => Math.Round(t.Bounds.Height)).Distinct());
    }

    /// <summary>The cooling bar spans the panels' full width and is on screen in dashboard-stats.png.</summary>
    private static void AssertCoolingCardFullWidth(Window window)
    {
        var panels = RectIn(window.FindControl<Grid>("HardwarePanels")!, window);
        var cooling = RectIn(window.FindControl<Border>("CoolingCard")!, window);
        Assert.Equal(panels.Width, cooling.Width, 1);
        Assert.Equal(panels.Left, cooling.Left, 1);
        Assert.True(cooling.Bottom <= window.Height, $"cooling card {cooling} should be on screen in dashboard-stats.png");
        Assert.True(cooling.Height < 140, $"cooling bar should be one compact row ({cooling.Height:F0}px)");
    }

    /// <summary>
    /// 4.1 thermal frame: four strips hugging the window edges, the shown temperature equal to max(CPU, GPU) (the first
    /// reading snaps), and the drawn line pixels on the window edge carry the frame-scale colour of that temperature.
    /// </summary>
    private static void AssertThermalFrame(Window window, MainWindowViewModel vm)
    {
        var frame = window.FindControl<ThermalFrame>("ThermalFrame")!;
        Assert.True(frame.IsEffectivelyVisible, "thermal frame should be visible");
        Assert.False(frame.IsHitTestVisible, "thermal frame must not block clicks or the resize border");
        Assert.Equal(new Size(window.Width, window.Height), frame.Bounds.Size);
        var strips = frame.Edges.Select(e => e.Bounds).ToList();
        Assert.Equal(4, strips.Count);
        Assert.Equal(0, strips[0].Top);                                  // top
        Assert.Equal(window.Width, strips[1].Right, 1);                  // right
        Assert.Equal(window.Height, strips[2].Bottom, 1);                // bottom
        Assert.Equal(0, strips[3].Left);                                 // left
        Assert.All(strips, s => Assert.True(Math.Min(s.Width, s.Height) <= 12, $"edge strip {s} should be thin"));

        var expected = vm.FrameTemperatureValue!.Value;
        Assert.Equal(Math.Max(vm.CpuGaugeTempValue!.Value, vm.GpuCoreTempValue!.Value), expected, 6);
        Assert.Equal(expected, frame.ShownTemperature, 1);
        Assert.Equal(ThermalFrameMath.ColorFor(expected), frame.CurrentColor);
        Assert.Equal(ThermalFrameMath.StateLabel(vm.FrameThermalState), vm.FrameTemperatureText.Split('·')[^1].Trim());

        // Real pixels: the outermost row in the middle of the top edge has the scale's dominant channel.
        var bitmap = window.CaptureRenderedFrame()!;
        using (bitmap)
        {
            var color = PixelAt(bitmap, bitmap.PixelSize.Width / 2, 0);
            var target = ThermalFrameMath.ColorFor(expected);
            static int Dominant(Color c) => c.R >= c.G && c.R >= c.B ? 0 : c.G >= c.B ? 1 : 2;
            Assert.True(Dominant(color) == Dominant(target),
                $"frame pixel {color} does not carry the {ThermalFrameMath.StateLabel(vm.FrameThermalState)} colour {target}");
        }
    }

    private static Color PixelAt(Avalonia.Media.Imaging.Bitmap bitmap, int x, int y)
    {
        var buffer = new byte[4];
        nint native = System.Runtime.InteropServices.Marshal.AllocHGlobal(4);
        try
        {
            bitmap.CopyPixels(new PixelRect(x, y, 1, 1), native, 4, 4);
            System.Runtime.InteropServices.Marshal.Copy(native, buffer, 0, 4);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(native);
        }
        // Byte order follows the frame's pixel format (headless captures are RGBA; BGRA elsewhere).
        bool rgba = bitmap.Format is { } format && format == Avalonia.Platform.PixelFormat.Rgba8888;
        return rgba
            ? Color.FromArgb(buffer[3], buffer[0], buffer[1], buffer[2])
            : Color.FromArgb(buffer[3], buffer[2], buffer[1], buffer[0]);
    }

    /// <summary>A themed toast card is on screen (inside the window) with its title and message laid out.</summary>
    private static void AssertToastDrawn(Window window, string expectedTitle)
    {
        var card = window.GetVisualDescendants().OfType<Avalonia.Controls.Notifications.NotificationCard>().Single();
        Assert.True(card.IsEffectivelyVisible, "toast should be visible");
        var rect = new Rect(card.TranslatePoint(default, window)!.Value, card.Bounds.Size);
        Assert.True(new Rect(0, 0, window.Width, window.Height).Contains(rect), $"toast {rect} is outside the window");
        Assert.Equal(new CornerRadius(14), card.CornerRadius);
        var title = card.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ToastTitleText");
        Assert.Equal(expectedTitle, title.Text);
        var message = card.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ToastMessageText");
        Assert.False(message.TextLayout.TextLines.Any(l => l.HasCollapsed), $"toast message '{message.Text}' is trimmed");
    }

    /// <summary>The status line is either wide enough to read or hidden — never a bare "…".</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AssertStatusMessageReadableOrHidden(Window window, bool? expectVisible)
    {
        var host = window.FindControl<Border>("StatusMessageHost")!;
        var text = window.FindControl<TextBlock>("StatusMessageText")!;
        bool wideEnough = host.Bounds.Width >= MainWindow.StatusMessageMinWidth;
        Assert.Equal(wideEnough, text.IsVisible);
        if (expectVisible is bool expected) Assert.Equal(expected, text.IsVisible);
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private sealed class Scope : IDisposable
    {
        public required MainWindowViewModel Vm { get; init; }
        public required FakeSettingsService Settings { get; init; }
        public required string TempDir { get; init; }

        public void Dispose()
        {
            Vm.Dispose();
            try { Directory.Delete(TempDir, recursive: true); } catch { }
        }
    }

    /// <summary>View model on fake services, after its first poll, with the requested health score.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static Scope CreateScope(SampleState state)
    {
        // A stub OpenRGB.exe so the RGB tab shows the installed/connected layout (never launched).
        string tempDir = Path.Combine(Path.GetTempPath(), "fanzi-render-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string fakeExe = Path.Combine(tempDir, "OpenRGB.exe");
        File.WriteAllText(fakeExe, "render harness stub");

        var rgbService = new FakeRgbService();
        var serverManager = new OpenRgbServerManager(() => fakeExe,
            (_, _) => Task.FromResult(OpenRgbInstallResult.Fail("render harness: downloads disabled")));
        var rgb = new RgbControlViewModel(rgbService, serverManager, utcClock: null, startBackgroundWork: false);
        // Fake game detector: the renders must not depend on what runs on the build machine.
        var gameDetector = new GameModeDetector(() => state.RunningGame is { } game ? (true, game) : (false, ""));
        var settings = new FakeSettingsService();
        var vm = new MainWindowViewModel(new FakeHardwareMonitorService(state), settings, rgb, gameDetector);

        PumpUntil(() => !vm.IsLoading && vm.CpuGaugeTempValue is not null && vm.FanChannels.Count > 0
                        && vm.LastUpdated.StartsWith("Updated", StringComparison.Ordinal),
            TimeSpan.FromSeconds(20), "first hardware poll");

        rgb.ConnectCommand.Execute(null);
        PumpUntil(() => rgb.Devices.Count > 0, TimeSpan.FromSeconds(10), "RGB device list");
        rgb.UpdateHardwareData(cpuTempC: state.CpuPackage, gpuTempC: state.GpuCore, cpuLoadPct: state.CpuLoad,
            pumpRpm: 2400 + state.CpuLoad * 4, isLiquidCooled: true);

        // The live scorer needs minutes of history; pin the score the scenario asks for.
        string grade = state.Health switch { >= 90 => "A+", >= 80 => "A", >= 70 => "B", >= 60 => "C", >= 40 => "D", _ => "F" };
        vm.HealthScoreValue = state.Health;
        vm.HealthScore = $"{state.Health:F0}";
        vm.HealthGrade = grade;
        vm.HealthSummary = $"System Health: {state.Health:F0}/100 ({grade})";

        SeedHistory(vm, state);

        return new Scope { Vm = vm, Settings = settings, TempDir = tempDir };
    }

    /// <summary>
    /// 15 minutes of 1 s samples ending on the scenario's live values: a gentle wave for the normal
    /// state, a climb into the red over the last few minutes for the hot one. Deterministic (seeded noise).
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void SeedHistory(MainWindowViewModel vm, SampleState s)
    {
        const int count = 900;
        bool hot = s.CpuPackage >= 90;
        var noise = new Random(42);
        var history = vm.History;
        history.Clear();
        var end = DateTime.UtcNow;

        // Offsets that are exactly 0 at x = 1 so every series ends on the current reading.
        static double Wave(double x, double a, double f1, double f2) =>
            a * Math.Sin(2 * Math.PI * f1 * x) + a * 0.45 * Math.Sin(2 * Math.PI * f2 * x + 1.3);
        // 1 until 55 % of the span, then a smooth fall to 0 at 95 % (the "heat-up" in the hot scenario).
        static double Ramp(double x)
        {
            double r = 1 - Math.Clamp((x - 0.55) / 0.4, 0, 1);
            return r * r * (3 - 2 * r);
        }

        double cpuEnd = Wave(1, 1, 3.2, 11), gpuEnd = Wave(1, 1, 2.1, 7.5);
        for (int i = 0; i < count; i++)
        {
            double x = (double)i / (count - 1);
            double jitter = i == count - 1 ? 0 : (noise.NextDouble() - 0.5) * 1.2;
            double cpuWave = 5.5 * (Wave(x, 1, 3.2, 11) - cpuEnd);
            double gpuWave = 4.0 * (Wave(x, 1, 2.1, 7.5) - gpuEnd);
            double cpu = hot ? s.CpuPackage - 24 * Ramp(x) + cpuWave * 0.35 : s.CpuPackage + cpuWave;
            double gpu = hot ? s.GpuCore - 22 * Ramp(x) + gpuWave * 0.35 : s.GpuCore + gpuWave;
            double load = Math.Clamp(s.CpuLoad + cpuWave * 3 - (hot ? 55 * Ramp(x) : 0), 0, 100);
            history.Record(end.AddSeconds(i - (count - 1)), cpu + jitter, gpu + jitter * 0.7, load,
                Math.Clamp(s.GpuLoad + gpuWave * 3, 0, 100), s.CpuFanRpm + cpuWave * 25);
        }
        vm.NotifyHistoryChanged();
    }

    private static void PumpUntil(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Render harness: timed out waiting for {what}.");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(15);
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Lets layout, bindings and the ~0.6 s gauge/brush transitions finish, then saves a frame.</summary>
    private static void Capture(TopLevel topLevel, string fileName, int expectedWidth, int expectedHeight)
    {
        for (int i = 0; i < 70; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(16);
        }
        Dispatcher.UIThread.RunJobs();

        // Every animated number has finished counting: the frame shows the real readings, not a mid-count value.
        foreach (var text in topLevel.GetVisualDescendants().OfType<TextBlock>())
        {
            if (AnimatedNumber.GetText(text) is string target && text.IsEffectivelyVisible)
                Assert.True(target == text.Text, $"{fileName}: number still counting ('{text.Text}' → '{target}')");
        }

        var frame = topLevel.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using (frame)
        {
            Assert.Equal(expectedWidth, frame!.PixelSize.Width);
            Assert.Equal(expectedHeight, frame.PixelSize.Height);

            string path = Path.Combine(ScreensDirectory, fileName);
            frame.Save(path);
            var info = new FileInfo(path);
            Assert.True(info.Exists && info.Length > 2_000, $"{fileName} was not written (or is empty)");
            Assert.True(info.LastWriteTimeUtc > DateTime.UtcNow.AddMinutes(-5), $"{fileName} is stale");
        }
    }

    private static string ScreensDirectory
    {
        get
        {
            string? overridden = Environment.GetEnvironmentVariable("FANZI_SCREENS_DIR");
            string dir = !string.IsNullOrWhiteSpace(overridden)
                ? overridden
                : Path.Combine(FindRepoRoot(), ".agents", "tasks", "fanzi-v2.2-polish", "screens");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FANZI.slnx")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("FANZI.slnx not found above " + AppContext.BaseDirectory);
    }
}
