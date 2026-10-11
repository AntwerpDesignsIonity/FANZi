using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fanzi.FanControl.Views;

namespace Fanzi.FanControl.Tests;

/// <summary>
/// Headless load check: the compiled XAML of the main window and every sub-view must load against the
/// shared FanziTheme (StaticResource keys resolve, styles/templates apply) without throwing.
/// </summary>
public class XamlLoadTests
{
    [AvaloniaFact]
    public void Theme_ExposesPaletteAndGradients()
    {
        var app = Application.Current!;
        foreach (var key in new[] { "AccentGradient", "CardBorderGradient", "GlowBorderGradient", "HeroBackground", "FanziBg0Brush", "FanziBorderBrush" })
            Assert.True(app.TryGetResource(key, null, out var value) && value is IBrush, $"missing theme brush '{key}'");

        var accent = Assert.IsType<LinearGradientBrush>(app.FindResource("AccentGradient"));
        Assert.Equal(new[] { Color.Parse("#2563EB"), Color.Parse("#7C3AED"), Color.Parse("#06B6D4") },
            accent.GradientStops.Select(s => s.Color).ToArray());
    }

    [AvaloniaFact]
    public void MainWindow_Loads()
    {
        if (!OperatingSystem.IsWindows()) return;

        var window = new MainWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void MainWindow_RgbTabIndex_MatchesRgbTabItem()
    {
        if (!OperatingSystem.IsWindows()) return;

        var window = new MainWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // The main TabControl is the one hosting the Dashboard tab; reordering its tabs must break this test.
        var tabs = window.GetVisualDescendants().OfType<TabControl>()
            .First(t => t.Items.OfType<TabItem>().Any(i => Equals(i.Header, "Dashboard")));
        var headers = tabs.Items.OfType<TabItem>().Select(i => i.Header as string).ToList();

        Assert.Equal(Fanzi.FanControl.ViewModels.MainWindowViewModel.RgbTabIndex, headers.IndexOf("RGB"));
        window.Close();
    }

    [AvaloniaFact]
    public void MiniOverlayWindow_Loads()
    {
        if (!OperatingSystem.IsWindows()) return;

        var window = new MiniOverlayWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsVisible);

        // Window stays transparent so the rounded border corners are not drawn on a square backdrop.
        Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(window.Background).Color);
        var root = window.FindControl<Border>("DragRoot")!;
        Assert.Equal(new CornerRadius(18), root.CornerRadius);

        // The transparent ring around the card swallows clicks, so it's only as wide as the glow blur.
        Assert.Equal(MiniOverlayWindow.ShadowMargin, root.Margin);
        Assert.True(root.Margin.Left <= root.BoxShadow[0].Blur + root.BoxShadow[0].Spread,
            $"margin {root.Margin.Left} exceeds the glow extent");
        Assert.Equal(280, window.Width - root.Margin.Left - root.Margin.Right);
        Assert.Equal(220, window.Height - root.Margin.Top - root.Margin.Bottom);
        Assert.IsType<LinearGradientBrush>(root.BorderBrush);
        Assert.Equal(2, window.GetVisualDescendants().OfType<Fanzi.FanControl.Controls.ThermalGauge>().Count());
        Assert.NotNull(window.FindControl<Fanzi.FanControl.Controls.ThermalHistoryChart>("CpuSparkline"));
        window.Close();
    }

    [AvaloniaFact]
    public void Theme_GivesInteractiveControlsTheAccentFocusRing()
    {
        var ring = Application.Current!.FindResource("FanziFocusRing");
        Assert.NotNull(ring);

        Control[] controls =
        {
            new Button(), new ToggleSwitch(), new Slider(), new RadioButton(), new CheckBox(),
            new Avalonia.Controls.Primitives.ToggleButton(),
        };
        var tabs = new TabControl { ItemsSource = new[] { new TabItem { Header = "A" } } };
        var window = new Window { Content = new StackPanel { Children = { tabs } }, Width = 400, Height = 400 };
        foreach (var c in controls) ((StackPanel)window.Content!).Children.Add(c);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            foreach (var control in controls.Append(window.GetVisualDescendants().OfType<TabItem>().First()))
                Assert.True(ReferenceEquals(ring, control.FocusAdorner), $"{control.GetType().Name} lacks the FANZi focus ring");

            // The ring is an accent outline drawn outside the control.
            var built = Assert.IsType<Border>(((ITemplate<Control>)ring!).Build());
            Assert.Equal(Color.Parse("#60A5FA"), Assert.IsAssignableFrom<ISolidColorBrush>(built.BorderBrush).Color);
            Assert.True(built.Margin.Left < 0);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SubViews_Load()
    {
        Control[] views =
        {
            new RgbControlView(), new SystemCleanerView(), new NetworkManagerView(),
            new PowerMonitorView(), new TaskManagerView(),
        };

        foreach (var view in views)
        {
            var window = new Window { Content = view, Width = 1200, Height = 900 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.Close();
        }
    }
}
