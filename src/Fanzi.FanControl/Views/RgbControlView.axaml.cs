using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Fanzi.FanControl.ViewModels;
using System.Threading.Tasks;

namespace Fanzi.FanControl.Views;

public partial class RgbControlView : UserControl
{
    public RgbControlView()
    {
        InitializeComponent();

        // Wire up slider → ViewModel callbacks once the view is loaded.
        this.AttachedToVisualTree += (_, _) =>
        {
            AttachSliderHandlers();
            AttachFilePicker();
        };
        DataContextChanged += (_, _) => AttachFilePicker();
    }

    /// <summary>Gives the ViewModel a file picker for "Locate OpenRGB.exe…".</summary>
    private void AttachFilePicker()
    {
        if (DataContext is RgbControlViewModel vm)
        {
            vm.PickOpenRgbExecutable = PickOpenRgbExecutableAsync;
            // 4.1: "Copy diagnostics" writes to the system clipboard.
            vm.CopyToClipboard = async text =>
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                    await clipboard.SetTextAsync(text);
            };
        }
    }

    private async Task<string?> PickOpenRgbExecutableAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return null;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Locate OpenRGB.exe",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                // Only OpenRGB.exe is accepted (OpenRgbServerManager.IsOpenRgbExecutableName).
                new FilePickerFileType("OpenRGB executable (OpenRGB.exe)") { Patterns = new[] { "OpenRGB.exe" } },
            },
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private void AttachSliderHandlers()
    {
        if (DataContext is not RgbControlViewModel vm) return;

        // Primary colour sliders
        PriR.PropertyChanged += (_, e) =>
        { if (e.Property.Name == nameof(Slider.Value)) vm.OnPrimarySliderChanged(); };
        PriG.PropertyChanged += (_, e) =>
        { if (e.Property.Name == nameof(Slider.Value)) vm.OnPrimarySliderChanged(); };
        PriB.PropertyChanged += (_, e) =>
        { if (e.Property.Name == nameof(Slider.Value)) vm.OnPrimarySliderChanged(); };

        // Secondary colour sliders
        SecR.PropertyChanged += (_, e) =>
        { if (e.Property.Name == nameof(Slider.Value)) vm.OnSecondarySliderChanged(); };
        SecG.PropertyChanged += (_, e) =>
        { if (e.Property.Name == nameof(Slider.Value)) vm.OnSecondarySliderChanged(); };
        SecB.PropertyChanged += (_, e) =>
        { if (e.Property.Name == nameof(Slider.Value)) vm.OnSecondarySliderChanged(); };

        // Hex text boxes
        PrimaryHexBox.LostFocus   += (_, _) => vm.HandlePrimaryHexInput(PrimaryHexBox.Text ?? "");
        SecondaryHexBox.LostFocus += (_, _) => vm.HandleSecondaryHexInput(SecondaryHexBox.Text ?? "");
    }
}
