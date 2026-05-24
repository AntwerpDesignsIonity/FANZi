using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Fanzi.FanControl.ViewModels;
using System;

namespace Fanzi.FanControl.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closed += OnClosed;
        PropertyChanged += OnPropertyChanged;

        var minimizeBtn = this.FindControl<Button>("MinimizeButton");
        var maximizeBtn = this.FindControl<Button>("MaximizeButton");
        var closeBtn = this.FindControl<Button>("CloseButton");

        if (minimizeBtn is not null)
            minimizeBtn.Click += (_, _) => WindowState = WindowState.Minimized;

        if (maximizeBtn is not null)
            maximizeBtn.Click += (_, _) =>
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        if (closeBtn is not null)
            closeBtn.Click += (_, _) => Close();
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
