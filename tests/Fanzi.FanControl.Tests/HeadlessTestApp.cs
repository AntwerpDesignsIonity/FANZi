using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(Fanzi.FanControl.Tests.HeadlessTestApp))]

namespace Fanzi.FanControl.Tests;

/// <summary>
/// Minimal application for [AvaloniaFact] tests: the same style stack as App.axaml
/// (Fluent → DataGrid → FanziTheme) without the tray icon or hardware services.
/// </summary>
public sealed class HeadlessTestApp : Application
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HeadlessTestApp>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        var baseUri = new Uri("avares://Fanzi.FanControl.Tests/");
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(baseUri) { Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml") });
        Styles.Add(new StyleInclude(baseUri) { Source = new Uri("avares://Fanzi.FanControl/Styles/FanziTheme.axaml") });
    }
}
