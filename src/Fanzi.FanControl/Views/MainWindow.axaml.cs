using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Fanzi.FanControl.ViewModels;
using System;
using System.Runtime.Versioning;

namespace Fanzi.FanControl.Views;

[SupportedOSPlatform("windows")]
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closed += OnClosed;
        PropertyChanged += OnPropertyChanged;

        var titleBar = this.FindControl<Border>("TitleBar");
        var minimizeBtn = this.FindControl<Button>("MinimizeButton");
        var maximizeBtn = this.FindControl<Button>("MaximizeButton");
        var closeBtn = this.FindControl<Button>("CloseButton");

        if (titleBar is not null)
        {
            titleBar.PointerPressed += OnTitleBarPointerPressed;
            titleBar.DoubleTapped += (_, _) =>
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        if (minimizeBtn is not null)
            minimizeBtn.Click += (_, _) => WindowState = WindowState.Minimized;

        if (maximizeBtn is not null)
            maximizeBtn.Click += (_, _) =>
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        if (closeBtn is not null)
            closeBtn.Click += (_, _) => Close();
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowStateProperty && DataContext is MainWindowViewModel vm)
        {
            if (WindowState == WindowState.Minimized && vm.MinimizeToTray)
                Hide();
        }

        if (e.Property == IsVisibleProperty && DataContext is MainWindowViewModel vm2)
        {
            vm2.SetWindowVisibility(IsVisible);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is IDisposable disposable)
            disposable.Dispose();
    }
}
