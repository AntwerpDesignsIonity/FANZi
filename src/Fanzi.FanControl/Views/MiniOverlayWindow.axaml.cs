using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Fanzi.FanControl.ViewModels;
using System;
using System.Runtime.Versioning;

namespace Fanzi.FanControl.Views;

[SupportedOSPlatform("windows")]
public partial class MiniOverlayWindow : Window
{
    private MainWindowViewModel? _vm;

    public MiniOverlayWindow()
    {
        InitializeComponent();

        var drag = this.FindControl<Border>("DragRoot");
        if (drag is not null)
        {
            drag.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                    BeginMoveDrag(e);
            };
        }

        var closeBtn = this.FindControl<Button>("CloseBtn");
        if (closeBtn is not null) closeBtn.Click += (_, _) => Hide();

        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            _vm = vm;
            ApplyTransparency();

            vm.PropertyChanged += (s, args) =>
            {
                if (args.PropertyName == nameof(MainWindowViewModel.OverlayTransparent) ||
                    args.PropertyName == nameof(MainWindowViewModel.OverlayOpacity))
                {
                    ApplyTransparency();
                }
            };
        }
    }

    private void ApplyTransparency()
    {
        if (_vm is null) return;

        byte alpha = (byte)(Math.Clamp(_vm.OverlayOpacity, 0.1, 1.0) * 255);

        if (_vm.OverlayTransparent)
        {
            // Use solid colour with alpha — avoids AcrylicBlur which is broken
            // on many Windows 11 builds (flickering, garbage pixels, ghost windows).
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
            TransparencyBackgroundFallback = new SolidColorBrush(Color.FromArgb(255, 5, 10, 18));
            Background = new SolidColorBrush(Color.FromArgb(alpha, 5, 10, 18));
        }
        else
        {
            // Fully opaque — no transparency at all.
            TransparencyLevelHint = new[] { WindowTransparencyLevel.None };
            TransparencyBackgroundFallback = new SolidColorBrush(Color.FromArgb(255, 8, 14, 24));
            Background = new SolidColorBrush(Color.FromArgb(255, 8, 14, 24));
        }
    }
}
