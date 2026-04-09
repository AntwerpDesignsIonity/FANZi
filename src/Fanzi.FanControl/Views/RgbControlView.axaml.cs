using Avalonia.Controls;
using Fanzi.FanControl.ViewModels;

namespace Fanzi.FanControl.Views;

public partial class RgbControlView : UserControl
{
    public RgbControlView()
    {
        InitializeComponent();

        // Wire up slider → ViewModel callbacks once the view is loaded.
        this.AttachedToVisualTree += (_, _) => AttachSliderHandlers();
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
