using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Fanzi.FanControl.Services;
using Fanzi.FanControl.ViewModels;
using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.Versioning;

namespace Fanzi.FanControl.Views;

[SupportedOSPlatform("windows")]
public partial class MiniOverlayWindow : Window
{
    private static readonly TimeSpan PositionSaveDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Transparent ring around the card that only exists so the glow isn't clipped. It is part of the
    /// window and would swallow clicks meant for whatever is behind it, so it equals the glow blur
    /// radius and nothing more; pointer presses landing in it are ignored (no drag, no menu).
    /// </summary>
    public static Thickness ShadowMargin { get; } = new(10);

    // Same navy as the cards; the transparent mode uses it with alpha from OverlayOpacity.
    private static readonly Color SurfaceColor = Color.FromRgb(0x0A, 0x12, 0x20);

    private readonly DispatcherTimer _positionSaveTimer;
    private MainWindowViewModel? _vm;
    private bool _positionRestored;
    private bool _suppressPositionSave;

    public MiniOverlayWindow()
    {
        InitializeComponent();

        _positionSaveTimer = new DispatcherTimer { Interval = PositionSaveDelay };
        _positionSaveTimer.Tick += OnPositionSaveTimerTick;

        DragRoot.PointerPressed += OnDragRootPointerPressed;
        PositionChanged += OnPositionChanged;
        DataContextChanged += OnDataContextChanged;
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
        if (_vm is not null)
            _vm.PropertyChanged -= OnViewModelPropertyChanged;

        _vm = DataContext as MainWindowViewModel;
        if (_vm is null) return;

        _vm.PropertyChanged += OnViewModelPropertyChanged;
        ApplyTransparency();
        UpdateHealthPill();
        ApplyLayoutMode(); // before RestorePosition: the clamp onto the screen uses the final height
        RestorePosition();
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
            case nameof(MainWindowViewModel.OverlayCompact):
                ApplyLayoutMode();
                break;
        }
    }

    // ── Layout mode (context menu: compact = gauges only, expanded = gauges + bars + sparkline) ──

    /// <summary>Window height of the expanded overlay: card 220 + 2 × <see cref="ShadowMargin"/>.</summary>
    public const double ExpandedHeight = 240;

    /// <summary>
    /// Expanded keeps the fixed 300 × 240 window. Compact hides the bars, sparkline and hint (bindings) and
    /// shrinks the window to the card's natural height; the top-left corner stays where the user put it.
    /// </summary>
    private void ApplyLayoutMode()
    {
        ApplyLayoutModeNow();
        // The IsVisible bindings may update after this handler; settle again once they have.
        Dispatcher.UIThread.Post(ApplyLayoutModeNow, DispatcherPriority.Loaded);
    }

    private void ApplyLayoutModeNow()
    {
        if (_vm is null) return;

        if (!_vm.OverlayCompact)
        {
            Height = ExpandedHeight;
            return;
        }

        // Bindings have already hidden the expanded-only rows; measure what is left at the card's width.
        DragRoot.Measure(new Size(Width, double.PositiveInfinity));
        double natural = Math.Ceiling(DragRoot.DesiredSize.Height); // includes ShadowMargin
        Height = natural > 0 ? Math.Min(natural, ExpandedHeight) : ExpandedHeight;
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

        if (_vm is not null)
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm = null;
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

    private void UpdateHealthPill()
    {
        if (_vm is null) return;

        var color = TemperatureColorScale.ForHealth(_vm.HealthScoreValue);
        HealthPill.Background = new SolidColorBrush(TemperatureColorScale.WithAlpha(color, 0.16));
        HealthPill.BorderBrush = new SolidColorBrush(TemperatureColorScale.WithAlpha(color, 0.6));
        HealthText.Foreground = new SolidColorBrush(color);
    }

    // ── Position persistence ──────────────────────────────────────────────────

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
            Position = OverlayPlacement.Resolve(_vm.OverlayX, _vm.OverlayY, new Size(Width, Height),
                screenAreas, primary.WorkingArea, primary.Scaling);
            _positionRestored = true;
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

    private void OnPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (_suppressPositionSave || _vm is null) return;

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
