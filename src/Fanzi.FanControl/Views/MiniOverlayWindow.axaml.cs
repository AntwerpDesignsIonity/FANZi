using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Fanzi.FanControl.Models;
using Fanzi.FanControl.Services;
using Fanzi.FanControl.ViewModels;
using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.Versioning;

namespace Fanzi.FanControl.Views;

/// <summary>
/// Floating always-on-top mini overlay (v4 Overlay Studio). Sizes itself to its content, can be scaled and
/// recoloured, remembers its position (clamped back onto a visible screen every time it is shown) and snaps
/// to screen corners from the context menu.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class MiniOverlayWindow : Window
{
    private static readonly TimeSpan PositionSaveDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RgbSyncInterval = TimeSpan.FromMilliseconds(66);

    /// <summary>
    /// Transparent ring around the card that only exists so the glow isn't clipped. It is part of the
    /// window and would swallow clicks meant for whatever is behind it, so it equals the glow blur
    /// radius and nothing more.
    /// </summary>
    public static Thickness ShadowMargin { get; } = new(10);

    /// <summary>Card width at 100 % (the window adds <see cref="ShadowMargin"/> on both sides).</summary>
    public const double CardWidth = 280;

    /// <summary>Upper bound of the expanded window height at 100 % with every row on (layout tests).</summary>
    public const double ExpandedHeight = 360;

    // Same navy as the cards; the transparent mode uses it with alpha from OverlayOpacity.
    private static readonly Color SurfaceColor = Color.FromRgb(0x0A, 0x12, 0x20);

    private readonly DispatcherTimer _positionSaveTimer;
    private MainWindowViewModel? _vm;
    private RgbControlViewModel? _rgb;
    private bool _positionRestored;
    private bool _suppressPositionSave;
    private DateTime _lastRgbSync = DateTime.MinValue;

    public MiniOverlayWindow()
    {
        InitializeComponent();

        _positionSaveTimer = new DispatcherTimer { Interval = PositionSaveDelay };
        _positionSaveTimer.Tick += OnPositionSaveTimerTick;

        DragRoot.PointerPressed += OnDragRootPointerPressed;
        PositionChanged += OnPositionChanged;
        DataContextChanged += OnDataContextChanged;
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void OnDragRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnHideClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null) _vm.HideMiniOverlay();
        else Hide();
    }

    // ── View model wiring ─────────────────────────────────────────────────────

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Unhook();

        _vm = DataContext as MainWindowViewModel;
        if (_vm is null) return;

        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.OverlayLayoutChanged += OnOverlayLayoutChanged;
        _vm.OverlaySnapRequested += OnSnapRequested;
        _rgb = _vm.RgbControl;
        if (_rgb is not null) _rgb.PropertyChanged += OnRgbPropertyChanged;

        ApplyTransparency();
        ApplyScale();
        ApplyAccent();
        UpdateHealthPill();
        RestorePosition(); // estimate before the first show; OnOpened clamps again with the real size
    }

    private void Unhook()
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.OverlayLayoutChanged -= OnOverlayLayoutChanged;
            _vm.OverlaySnapRequested -= OnSnapRequested;
        }
        if (_rgb is not null) _rgb.PropertyChanged -= OnRgbPropertyChanged;
        _vm = null;
        _rgb = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.OverlayTransparent):
            case nameof(MainWindowViewModel.OverlayOpacity):
                ApplyTransparency();
                break;
            case nameof(MainWindowViewModel.HealthScoreValue):
                UpdateHealthPill();
                break;
            case nameof(MainWindowViewModel.OverlayScale):
                ApplyScale();
                break;
            case nameof(MainWindowViewModel.OverlayAccent):
                ApplyAccent();
                break;
        }
    }

    private void OnRgbPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RgbControlViewModel.PreviewColor) || _vm is null || !IsVisible) return;
        if (!OverlayAccent.ByName(_vm.OverlayAccent).IsRgbSync) return;

        // PreviewColor ticks at 30 fps; ~15 fps is plenty for a border tint.
        var now = DateTime.UtcNow;
        if (now - _lastRgbSync < RgbSyncInterval) return;
        _lastRgbSync = now;
        Dispatcher.UIThread.Post(ApplyAccent, DispatcherPriority.Background);
    }

    private void OnOverlayLayoutChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(KeepOnScreen, DispatcherPriority.Loaded);

    private void OnOpened(object? sender, EventArgs e)
    {
        _positionRestored = false;
        RestorePosition();
        // Once more after the first layout pass, when SizeToContent has produced the real size.
        Dispatcher.UIThread.Post(() => { _positionRestored = false; RestorePosition(); }, DispatcherPriority.Loaded);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // Flush a pending (debounced) position save before letting go of the view model.
        if (_positionSaveTimer.IsEnabled)
        {
            _positionSaveTimer.Stop();
            SavePosition();
        }
        _positionSaveTimer.Tick -= OnPositionSaveTimerTick;
        Unhook();
    }

    /// <summary>
    /// Called after every Show: re-asserts Topmost (Windows can drop it after display changes) and pulls the
    /// window back onto a screen if a monitor was unplugged or the resolution changed while it was hidden.
    /// </summary>
    public void BringIntoView()
    {
        if (_vm?.OverlayTopmost == true)
        {
            Topmost = false;
            Topmost = true;
        }
        Dispatcher.UIThread.Post(KeepOnScreen, DispatcherPriority.Loaded);
    }

    // ── Look ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The window stays transparent in both modes (so the rounded corners never show a square
    /// backdrop); only the DragRoot surface changes: alpha tint when transparent, solid otherwise.
    /// Solid colour with alpha avoids AcrylicBlur, which is broken on many Windows 11 builds.
    /// </summary>
    private void ApplyTransparency()
    {
        if (_vm is null) return;

        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };

        DragRoot.Background = _vm.OverlayTransparent
            ? new SolidColorBrush(TemperatureColorScale.WithAlpha(SurfaceColor, Math.Clamp(_vm.OverlayOpacity, 0.1, 1.0)))
            : new SolidColorBrush(SurfaceColor);
    }

    private void ApplyScale()
    {
        if (_vm is null) return;
        double s = Math.Clamp(_vm.OverlayScale, MainWindowViewModel.OverlayScaleMin, MainWindowViewModel.OverlayScaleMax);
        ScaleHost.LayoutTransform = new ScaleTransform(s, s);
        Dispatcher.UIThread.Post(KeepOnScreen, DispatcherPriority.Loaded);
    }

    /// <summary>Border gradient, glow and title in the chosen accent; RGB Sync uses the live effect colour.</summary>
    private void ApplyAccent()
    {
        if (_vm is null) return;
        var accent = OverlayAccent.ByName(_vm.OverlayAccent);

        Color start = Color.Parse(accent.Start);
        Color end = Color.Parse(accent.End);
        Color glow = Color.Parse(accent.Glow);
        if (accent.IsRgbSync && _rgb is not null)
        {
            var live = _rgb.PreviewColor;
            start = Color.FromRgb(live.R, live.G, live.B);
            end = Color.FromRgb((byte)(255 - live.R / 2), (byte)(255 - live.G / 2), (byte)(255 - live.B / 2));
            glow = TemperatureColorScale.WithAlpha(start, 0.45);
        }

        var gradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(start, 0), new GradientStop(end, 1) },
        };
        DragRoot.BorderBrush = gradient;
        OverlayTitle.Foreground = gradient;
        DragRoot.BoxShadow = new BoxShadows(
            new BoxShadow { Blur = 10, Color = glow },
            new[] { new BoxShadow { OffsetY = 1, Color = Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF), IsInset = true } });
    }

    private void UpdateHealthPill()
    {
        if (_vm is null) return;

        var color = TemperatureColorScale.ForHealth(_vm.HealthScoreValue);
        HealthPill.Background = new SolidColorBrush(TemperatureColorScale.WithAlpha(color, 0.16));
        HealthPill.BorderBrush = new SolidColorBrush(TemperatureColorScale.WithAlpha(color, 0.6));
        HealthText.Foreground = new SolidColorBrush(color);
    }

    // ── Position persistence ──────────────────────────────────────────────────

    /// <summary>Current logical window size (falls back to a measure before the first layout pass).</summary>
    private Size CurrentLogicalSize()
    {
        // SizeToContent: the content's desired size is the window size. Bounds lag behind (they still hold the
        // platform's default size before the first layout), so they are only a fallback.
        if (Content is Control root)
        {
            root.Measure(Size.Infinity);
            if (root.DesiredSize.Width > 1 && root.DesiredSize.Height > 1) return root.DesiredSize;
        }
        if (Bounds.Width > 1 && Bounds.Height > 1) return Bounds.Size;
        return new Size(CardWidth + ShadowMargin.Left + ShadowMargin.Right, 240);
    }

    /// <summary>Restores the saved position clamped onto a screen, or top-right of the primary screen.</summary>
    private void RestorePosition()
    {
        if (_vm is null || _positionRestored) return;

        try
        {
            var screens = Screens;
            var primary = screens?.Primary ?? screens?.All.FirstOrDefault();
            if (screens is null || primary is null) return;

            // Each screen converts the logical size with its own scaling (mixed-DPI setups).
            var screenAreas = screens.All.Select(s => (s.WorkingArea, s.Scaling)).ToList();

            _suppressPositionSave = true;
            Position = OverlayPlacement.Resolve(_vm.OverlayX, _vm.OverlayY, CurrentLogicalSize(),
                screenAreas, primary.WorkingArea, primary.Scaling);
            _positionRestored = IsVisible;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Restoring the overlay position failed", ex);
        }
        finally
        {
            _suppressPositionSave = false;
        }
    }

    /// <summary>Clamps the window fully onto the screen it is on (after resizes, scale changes and re-shows).</summary>
    private void KeepOnScreen()
    {
        try
        {
            var screens = Screens;
            if (screens is null || !IsVisible) return;
            var screen = screens.ScreenFromWindow(this) ?? screens.Primary ?? screens.All.FirstOrDefault();
            if (screen is null) return;

            var size = PixelSize.FromSize(CurrentLogicalSize(), screen.Scaling);
            var clamped = OverlayPlacement.ClampInto(Position, size, screen.WorkingArea);
            if (clamped != Position) Position = clamped;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Keeping the overlay on screen failed", ex);
        }
    }

    private void OnSnapRequested(object? sender, string corner)
    {
        try
        {
            var screens = Screens;
            var screen = screens?.ScreenFromWindow(this) ?? screens?.Primary;
            if (screen is null) return;

            var area = screen.WorkingArea;
            var size = PixelSize.FromSize(CurrentLogicalSize(), screen.Scaling);
            int m = OverlayPlacement.DefaultMargin;
            int left = area.X + m, right = area.Right - size.Width - m;
            int top = area.Y + m, bottom = area.Bottom - size.Height - m;
            var target = corner switch
            {
                "TopLeft" => new PixelPoint(left, top),
                "BottomLeft" => new PixelPoint(left, bottom),
                "BottomRight" => new PixelPoint(right, bottom),
                _ => new PixelPoint(right, top),
            };
            Position = OverlayPlacement.ClampInto(target, size, area);
            SavePosition();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Snapping the overlay failed", ex);
        }
    }

    private void OnPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (_suppressPositionSave || _vm is null || !IsVisible) return;

        // Debounce: dragging fires many events; save once the window has rested for 500 ms.
        _positionSaveTimer.Stop();
        _positionSaveTimer.Start();
    }

    private void OnPositionSaveTimerTick(object? sender, EventArgs e)
    {
        _positionSaveTimer.Stop();
        SavePosition();
    }

    private void SavePosition()
    {
        try
        {
            _vm?.SaveOverlayPosition(Position.X, Position.Y);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Saving the overlay position failed", ex);
        }
    }
}
