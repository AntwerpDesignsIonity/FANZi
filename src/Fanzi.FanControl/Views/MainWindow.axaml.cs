using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
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
    }

    private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowStateProperty && DataContext is MainWindowViewModel vm)
        {
            if (WindowState == WindowState.Minimized && vm.MinimizeToTray)
            {
                Hide();
            }
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
