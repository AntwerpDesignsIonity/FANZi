using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.ViewModels;

/// <summary>
/// 4.4 Fans tab: "Use BIOS fan settings". On (the default) every fan on a motherboard header follows the BIOS Smart Fan
/// curve — FANZi hands the headers back and only reads them; GPU and fan-hub channels stay under FANZi.
/// See <see cref="IHardwareMonitorService.BiosFanControl"/>.
/// </summary>
public partial class MainWindowViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BiosFanControlStatus))]
    private bool _useBiosFanControl;

    public string BiosFanControlStatus => UseBiosFanControl
        ? "On — motherboard fan headers follow your BIOS Smart Fan settings. FANZi only reads them; GPU and fan-hub channels stay controllable."
        : "Off — FANZi can set motherboard fan headers (manual duty, AI Auto-Fan, curves). Auto hands a header back to the BIOS.";

    partial void OnUseBiosFanControlChanged(bool value)
    {
        _hardwareMonitorService.BiosFanControl = value;
        if (value)
        {
            // AI Auto-Fan's last duty no longer applies — the BIOS owns the CPU header now.
            _lastAiAppliedPercent = null;
            _lastAiAppliedChannelId = null;
        }

        if (_suppressProfileSync) return;
        _appSettings.FanBiosControl = value;
        _ = SaveSettingsAsync();
        StatusMessage = value
            ? "BIOS fan control on: motherboard fan headers are back on your BIOS Smart Fan settings."
            : "BIOS fan control off: FANZi can now drive the motherboard fan headers.";
        _ = SafeExecuteAsync(RefreshAsync, "Refresh after BIOS fan control change");
    }
}
