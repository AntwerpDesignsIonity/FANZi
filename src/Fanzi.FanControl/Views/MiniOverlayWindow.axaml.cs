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

        // Apply transparency from settings when DataContext is set
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            _vm = vm;
            ApplyTransparency();

            // Listen for changes
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

        if (_vm.OverlayTransparent)
        {
            // Transparent mode: acrylic blur + semi-transparent background
            TransparencyLevelHint = new[] { Avalonia.Controls.WindowTransparencyLevel.AcrylicBlur };
            byte alpha = (byte)(Math.Clamp(_vm.OverlayOpacity, 0.1, 1.0) * 255);
            Background = new SolidColorBrush(Color.FromArgb(alpha, 5, 10, 18));
        }
        else
        {
            // Opaque mode: solid dark background, no blur
            TransparencyLevelHint = new[] { Avalonia.Controls.WindowTransparencyLevel.None };
            Background = new SolidColorBrush(Color.FromArgb(255, 8, 14, 24));
        }
    }
}
