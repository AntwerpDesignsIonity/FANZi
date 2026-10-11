using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fanzi.FanControl.Models;
using Fanzi.FanControl.Views;

namespace Fanzi.FanControl.Tests;

/// <summary>Toasts render through Avalonia's WindowNotificationManager with the FanziTheme card styling.</summary>
public sealed class ToastContentTests
{
    [Theory]
    [InlineData(AppAlertLevel.Information, NotificationType.Information)]
    [InlineData(AppAlertLevel.Success, NotificationType.Success)]
    [InlineData(AppAlertLevel.Warning, NotificationType.Warning)]
    [InlineData(AppAlertLevel.Error, NotificationType.Error)]
    public void Levels_MapOntoNotificationTypes(AppAlertLevel level, NotificationType expected)
        => Assert.Equal(expected, ToastContent.ToNotificationType(level));

    [Fact]
    public void Errors_StayLongerThanConfirmations()
        => Assert.True(ToastContent.Expiration(AppAlertLevel.Error) > ToastContent.Expiration(AppAlertLevel.Success));

    [AvaloniaFact]
    public void Toast_ShowsAThemedCardWithTitleAndMessage()
    {
        var window = new Window { Width = 1000, Height = 700 };
        window.Show();
        try
        {
            // Created lazily on an already-shown window, exactly like MainWindow's first toast.
            var manager = ToastContent.CreateHost(window, 3);
            var alert = new AppNotification(AppAlertKind.TemperatureHigh, AppAlertLevel.Warning, "CPU temperature high",
                "96°C — above your 95°C alert threshold.", DateTime.UtcNow);

            manager.Show(ToastContent.Build(alert), ToastContent.ToNotificationType(alert.Level), TimeSpan.FromSeconds(30));
            Dispatcher.UIThread.RunJobs();

            var card = Assert.Single(window.GetVisualDescendants().OfType<NotificationCard>());
            Assert.Contains(":warning", card.Classes);
            Assert.Equal(new Avalonia.CornerRadius(14), card.CornerRadius); // FanziTheme, not Fluent's 4
            Assert.Equal(360, card.Width);
            var texts = card.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("CPU temperature high", texts);
            Assert.Contains("96°C — above your 95°C alert threshold.", texts);
            Assert.Contains("!", texts);
        }
        finally
        {
            window.Close();
        }
    }
}
