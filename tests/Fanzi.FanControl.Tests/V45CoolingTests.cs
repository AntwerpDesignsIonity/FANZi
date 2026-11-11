using Fanzi.FanControl.Models;
using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.Tests;

/// <summary>4.5: pump detection and protection, CPU-cooling selection.</summary>
public class V45CoolingTests
{
    [Theory]
    [InlineData("Pump", true)]
    [InlineData("AIO Pump", true)]
    [InlineData("W_PUMP+", true)]
    [InlineData("AIO_PUMP", true)]
    [InlineData("Pump Fan", true)]
    [InlineData("CPU Fan", false)]
    [InlineData("System Fan #2", false)]
    [InlineData("Fan #5", false)]
    [InlineData("Chassis Fan", false)]
    public void Pump_names_are_matched_by_whole_token(string name, bool pump)
        => Assert.Equal(pump, CoolingChannelClassifier.NameSaysPump(name));

    [Fact]
    public void Classifier_roles_from_names()
    {
        Assert.Equal(CoolingRole.CpuFan, CoolingChannelClassifier.Classify("CPU Fan", "SuperIO", "Nuvoton NCT6799D").Role);
        Assert.Equal(CoolingRole.SystemFan, CoolingChannelClassifier.Classify("System Fan #1", "SuperIO", "Nuvoton NCT6799D").Role);
        var pump = CoolingChannelClassifier.Classify("Pump Fan", "SuperIO", "Nuvoton NCT6799D");
        Assert.True(pump.IsPump);
        Assert.Equal(CoolingRole.Pump, pump.Role);
        Assert.Equal(CoolingEvidence.Name, pump.Evidence);
    }

    [Fact]
    public void User_override_beats_every_other_signal()
    {
        var asFan = CoolingChannelClassifier.Classify("Pump", "SuperIO", "NCT6799D", CoolingRole.SystemFan, rpmSaysPump: true);
        Assert.False(asFan.IsPump);
        Assert.Equal(CoolingEvidence.UserOverride, asFan.Evidence);

        var asPump = CoolingChannelClassifier.Classify("Fan #3", "SuperIO", "NCT6799D", CoolingRole.Pump);
        Assert.True(asPump.IsPump);
        Assert.Equal(CoolingEvidence.UserOverride, asPump.Evidence);
    }

    [Fact]
    public void Anonymous_header_with_a_pump_rpm_signature_is_a_pump()
    {
        var c = CoolingChannelClassifier.Classify("Fan #3", "SuperIO", "NCT6799D", rpmSaysPump: true);
        Assert.True(c.IsPump);
        Assert.Equal(CoolingEvidence.RpmSignature, c.Evidence);
        // …but a header the board names "CPU Fan" is not re-labelled by RPM alone.
        Assert.False(CoolingChannelClassifier.Classify("System Fan #2", "SuperIO", "NCT6799D", rpmSaysPump: false).IsPump);
    }

    [Fact]
    public void Rpm_signature_needs_fast_flat_and_faster_than_peers()
    {
        double[] flatFast = Enumerable.Repeat(2800.0, 60).ToArray();
        Assert.True(PumpSignatureTracker.LooksLikePump(flatFast, new[] { 900.0, 1100.0 }));
        // No peers → cannot tell a pump from a lone fast fan.
        Assert.False(PumpSignatureTracker.LooksLikePump(flatFast, Array.Empty<double>()));
        // Slow.
        Assert.False(PumpSignatureTracker.LooksLikePump(Enumerable.Repeat(1200.0, 60).ToArray(), new[] { 600.0 }));
        // Swings like a fan on a curve.
        double[] swinging = Enumerable.Range(0, 60).Select(i => 2000.0 + (i % 2 == 0 ? 500 : -500)).ToArray();
        Assert.False(PumpSignatureTracker.LooksLikePump(swinging, new[] { 900.0 }));
        // Not clearly faster than the fans.
        Assert.False(PumpSignatureTracker.LooksLikePump(flatFast, new[] { 2500.0 }));
    }

    [Fact]
    public void Tracker_latches_after_enough_samples()
    {
        var t = new PumpSignatureTracker();
        for (int i = 0; i < PumpSignatureTracker.MinSamples + 5; i++)
            t.Record(new (string, string, double?)[] { ("p", "board", 2900 + i % 3), ("f1", "board", 800 + i * 4), ("f2", "board", 1000 - i * 2) });
        Assert.True(t.IsLikelyPump("p"));
        Assert.False(t.IsLikelyPump("f1"));
        Assert.False(t.IsLikelyPump("f2"));
    }

    [Theory]
    [InlineData(0, 100, 60)]
    [InlineData(30, 100, 60)]
    [InlineData(75, 100, 75)]
    [InlineData(100, 100, 100)]
    [InlineData(100, 80, 80)]
    [InlineData(50, 40, 60)] // a cap below the floor never wins
    public void Pump_duty_is_clamped_between_floor_and_cap(double requested, double cap, double expected)
        => Assert.Equal(expected, PumpProtection.Clamp(requested, cap));

    private static FanChannelSnapshot Fan(string id, double? rpm, CoolingRole role, FanDeviceKind kind = FanDeviceKind.Fan, int index = 0)
        => new(id, id, rpm, 50, true, kind, "", "board", index) { Role = role };

    [Fact]
    public void Cpu_cooling_prefers_the_cpu_fan_over_a_faster_pump()
    {
        var fans = new[]
        {
            Fan("pump", 3000, CoolingRole.Pump, FanDeviceKind.Pump),
            Fan("sys1", 900, CoolingRole.SystemFan, index: 2),
            Fan("cpu", 1100, CoolingRole.CpuFan, index: 1),
        };
        Assert.Equal("cpu", HardwareMonitorService.SelectCpuCooling(fans)!.Id);
    }

    [Fact]
    public void Cpu_cooling_never_picks_the_fastest_spinner_as_a_fan()
    {
        // No CPU-fan header, a pump and anonymous fans: the pump is shown (read-only), never a "fastest fan".
        var fans = new[]
        {
            Fan("f2", 1400, CoolingRole.Auto, index: 2),
            Fan("pump", 3000, CoolingRole.Pump, FanDeviceKind.Pump, index: 5),
            Fan("f1", 700, CoolingRole.Auto, index: 1),
        };
        var picked = HardwareMonitorService.SelectCpuCooling(fans)!;
        Assert.Equal("pump", picked.Id);
        Assert.True(picked.IsPump);

        // Anonymous fans only: the lowest-numbered spinning header, not the fastest.
        var anon = new[] { Fan("f3", 2200, CoolingRole.Auto, index: 3), Fan("f1", 700, CoolingRole.Auto, index: 1), Fan("f0", 0, CoolingRole.Auto, index: 0) };
        Assert.Equal("f1", HardwareMonitorService.SelectCpuCooling(anon)!.Id);
    }

    [Fact]
    public void Role_text_round_trips()
    {
        foreach (var role in Enum.GetValues<CoolingRole>())
            Assert.Equal(role, CoolingChannelClassifier.ParseRole(role.ToString()));
        Assert.Equal(CoolingRole.Auto, CoolingChannelClassifier.ParseRole("nonsense"));
        Assert.Equal(CoolingRole.Auto, CoolingChannelClassifier.ParseRole(null));
    }
}
