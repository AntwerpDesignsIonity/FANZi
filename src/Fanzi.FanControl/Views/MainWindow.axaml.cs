using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Interactivity;
using Fanzi.FanControl.Models;
using Fanzi.FanControl.Services;
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

        var statusHost = this.FindControl<Border>("StatusMessageHost");
        var statusText = this.FindControl<TextBlock>("StatusMessageText");
        if (statusHost is not null && statusText is not null)
            statusHost.SizeChanged += (_, e) => statusText.IsVisible = e.NewSize.Width >= StatusMessageMinWidth;

        DataContextChanged += (_, _) => AttachToasts(DataContext as MainWindowViewModel);

        // Bubbling handler: a focused control that uses the key itself (text box, curve editor) wins.
        KeyDown += OnWindowKeyDown;
    }

    // ── Keyboard shortcuts (Ctrl+1…9 tabs, Ctrl+O overlay, F5 refresh, F1 help) ────

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || DataContext is not MainWindowViewModel vm) return;
        if (KeyboardShortcuts.Resolve(e.Key, e.KeyModifiers) is { } shortcut && vm.ExecuteShortcut(shortcut))
            e.Handled = true;
    }

    // ── Toast notifications ────────────────────────────────────────────────

    private MainWindowViewModel? _toastSource;
    private WindowNotificationManager? _toastManager;

    /// <summary>Toasts currently on screen at most; older ones are pushed out.</summary>
    internal const int MaxToasts = 3;

    /// <summary>Notification host in the window's adorner layer (null until the first toast).</summary>
    internal WindowNotificationManager? ToastManager => _toastManager;

    private void AttachToasts(MainWindowViewModel? vm)
    {
        if (ReferenceEquals(vm, _toastSource)) return;
        if (_toastSource is not null)
        {
            _toastSource.ToastRequested -= OnToastRequested;
            _toastSource.OverlayStudioRequested -= OnOverlayStudioRequested;
        }
        _toastSource = vm;
        if (vm is not null)
        {
            vm.ToastRequested += OnToastRequested;
            vm.OverlayStudioRequested += OnOverlayStudioRequested;
        }
    }

    /// <summary>Overlay context menu → "Overlay Studio…": the Settings tab is selected; scroll the card into view.</summary>
    private void OnOverlayStudioRequested(object? sender, EventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => this.FindControl<Border>("OverlayStudioCard")?.BringIntoView(),
            Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void OnToastRequested(object? sender, AppNotification notification)
    {
        try
        {
            _toastManager ??= ToastContent.CreateHost(this, MaxToasts);
            _toastManager.Show(
                ToastContent.Build(notification),
                ToastContent.ToNotificationType(notification.Level),
                ToastContent.Expiration(notification.Level),
                onClick: () => _toastSource?.OpenAlertTarget(notification));
        }
        catch (Exception ex)
        {
            // A toast is never worth a crash: the alert is already in the log.
            AppLog.Warn("Showing a toast notification failed", ex);
        }
    }

    /// <summary>Below this width the status line would show little more than "…", so it is hidden
    /// (the text stays available as the host's tooltip).</summary>
    internal const double StatusMessageMinWidth = 140;

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowStateProperty && DataContext is MainWindowViewModel vm)
        {
            vm.SetWindowMinimized(WindowState == WindowState.Minimized);
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
        // The view model is owned by App and disposed on desktop.Exit (after fan control is
        // restored) — disposing it here left the process running with a dead UI.
        PropertyChanged -= OnPropertyChanged;
        KeyDown -= OnWindowKeyDown;
        AttachToasts(null);
    }
}
