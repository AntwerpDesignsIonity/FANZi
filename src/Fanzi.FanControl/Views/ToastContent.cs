using System;
using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Layout;
using Fanzi.FanControl.Models;

namespace Fanzi.FanControl.Views;

/// <summary>
/// Builds the content of a toast (icon badge, title, message, time) styled by the theme's
/// <c>toast-*</c> classes; the surrounding NotificationCard is themed in FanziTheme.axaml.
/// </summary>
internal static class ToastContent
{
    public static NotificationType ToNotificationType(AppAlertLevel level) => level switch
    {
        AppAlertLevel.Success => NotificationType.Success,
        AppAlertLevel.Warning => NotificationType.Warning,
        AppAlertLevel.Error => NotificationType.Error,
        _ => NotificationType.Information,
    };

    /// <summary>Errors stay longest so they can be read; confirmations go quickly.</summary>
    public static TimeSpan Expiration(AppAlertLevel level) => level switch
    {
        AppAlertLevel.Error => TimeSpan.FromSeconds(10),
        AppAlertLevel.Warning => TimeSpan.FromSeconds(8),
        _ => TimeSpan.FromSeconds(5),
    };

    /// <summary>
    /// Creates the toast host in <paramref name="host"/>'s adorner layer, bottom-right, at most
    /// <paramref name="maxItems"/> cards. The template is applied right away: <c>Show</c> silently drops a
    /// toast while the manager has no item panel yet (first toast after a lazy creation).
    /// </summary>
    public static WindowNotificationManager CreateHost(TopLevel host, int maxItems)
    {
        var manager = new WindowNotificationManager(host)
        {
            Position = NotificationPosition.BottomRight,
            MaxItems = maxItems,
            Margin = new Avalonia.Thickness(0, 0, 8, 8),
        };
        manager.ApplyTemplate();
        return manager;
    }

    public static Control Build(AppNotification notification)
    {
        string levelClass = notification.Level switch
        {
            AppAlertLevel.Success => "success",
            AppAlertLevel.Warning => "warning",
            AppAlertLevel.Error => "error",
            _ => "info",
        };
        string glyph = notification.Level switch
        {
            AppAlertLevel.Success => "✓",
            AppAlertLevel.Information => "i",
            _ => "!",
        };

        var icon = new Border { Child = new TextBlock { Text = glyph } };
        icon.Classes.Add("toast-icon");
        icon.Classes.Add(levelClass);

        var title = new TextBlock { Text = notification.Title, Name = "ToastTitleText" };
        title.Classes.Add("toast-title");
        var message = new TextBlock { Text = notification.Message, Name = "ToastMessageText" };
        message.Classes.Add("toast-message");
        var time = new TextBlock
        {
            Text = "FANZi · " + notification.TimeUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture),
        };
        time.Classes.Add("toast-time");

        var text = new StackPanel { Spacing = 3, Children = { title, message, time } };
        Grid.SetColumn(text, 1);

        var root = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 12,
            Margin = new Avalonia.Thickness(14, 12, 14, 12),
            VerticalAlignment = VerticalAlignment.Top,
            Children = { icon, text },
        };
        root.Classes.Add("toast");
        AutomationProperties.SetName(root, notification.Summary);
        return root;
    }
}
