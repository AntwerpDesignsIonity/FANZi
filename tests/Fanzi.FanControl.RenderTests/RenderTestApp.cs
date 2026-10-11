using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(Fanzi.FanControl.RenderTests.RenderTestApp))]

namespace Fanzi.FanControl.RenderTests;

/// <summary>
/// Same style stack as App.axaml (Fluent → DataGrid → FanziTheme) but rendered for real:
/// Skia + Inter, headless drawing off, so captured frames contain actual pixels.
/// </summary>
public sealed class RenderTestApp : Application
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<RenderTestApp>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        var baseUri = new Uri("avares://Fanzi.FanControl.RenderTests/");
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(baseUri) { Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml") });
        Styles.Add(new StyleInclude(baseUri) { Source = new Uri("avares://Fanzi.FanControl/Styles/FanziTheme.axaml") });
    }
}

/// <summary>
/// [AvaloniaFact] (same discoverer: runs on the headless UI thread) that only runs with
/// FANZI_RENDER_TESTS=1 (it writes PNGs and takes a while); otherwise it reports as skipped.
/// AvaloniaFactAttribute is sealed, hence the explicit discoverer.
/// </summary>
[Xunit.Sdk.XunitTestCaseDiscoverer("Avalonia.Headless.XUnit.AvaloniaUIFactDiscoverer", "Avalonia.Headless.XUnit")]
[AttributeUsage(AttributeTargets.Method)]
public sealed class RenderFactAttribute : FactAttribute
{
    public const string EnvVar = "FANZI_RENDER_TESTS";

    public RenderFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(EnvVar) != "1")
            Skip = $"Render test — set {EnvVar}=1 to render the UI screenshots.";
    }
}
