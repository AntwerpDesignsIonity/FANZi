using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Fanzi.FanControl.AI;
using Fanzi.FanControl.Models;
using Fanzi.FanControl.Services;
using Fanzi.FanControl.ViewModels;
using Xunit;

namespace Fanzi.FanControl.Tests;

/// <summary>4.4 AEDi Cooling Fingerprint + Self-Doctor.</summary>
public class AediCoolingAndDoctorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    /// <summary>Feeds one 5-minute window: power swings 20–120 W, temperature = ambient + R·P (+ small noise).</summary>
    private static void FeedWindow(CoolingFingerprint fp, DateTimeOffset start, double kPerW, double ambient = 30, double rpm = 1200)
    {
        var rnd = new Random(42);
        for (int s = 0; s <= 300; s++)
        {
            double p = 20 + 100 * (0.5 + 0.5 * Math.Sin(s / 15.0));
            double t = ambient + kPerW * p + (rnd.NextDouble() - 0.5) * 0.6;
            fp.Record(start.AddSeconds(s), t, p, rpm);
        }
    }

    [Fact]
    public void Fit_recovers_the_slope()
    {
        double[] x = { 10, 20, 30, 40, 50 };
        double[] y = x.Select(v => 35 + 0.4 * v).ToArray();
        Assert.True(CoolingFingerprint.TryFit(x, y, out double slope, out double r2));
        Assert.Equal(0.4, slope, 6);
        Assert.Equal(1.0, r2, 6);
        Assert.False(CoolingFingerprint.TryFit(new double[] { 5, 5, 5 }, new double[] { 1, 2, 3 }, out _, out _));
    }

    [Fact]
    public void Learns_then_reports_healthy_cooling()
    {
        var fp = new CoolingFingerprint();
        Assert.Equal(CoolingState.Learning, fp.LastReport.State);
        for (int w = 0; w < CoolingFingerprint.BaselineObservations; w++)
            FeedWindow(fp, T0.AddMinutes(w * 6), 0.40);
        var r = fp.Evaluate(T0.AddHours(2));
        Assert.Equal(CoolingState.Healthy, r.State);
        Assert.InRange(r.EfficiencyPercent!.Value, 97, 103);
        Assert.InRange(r.CurrentKPerWatt!.Value, 0.38, 0.42);
    }

    [Fact]
    public void Ambient_change_alone_is_not_degradation()
    {
        var fp = new CoolingFingerprint();
        for (int w = 0; w < CoolingFingerprint.BaselineObservations; w++) FeedWindow(fp, T0.AddMinutes(w * 6), 0.40, ambient: 22);
        for (int w = 0; w < 12; w++) FeedWindow(fp, T0.AddDays(1).AddMinutes(w * 6), 0.40, ambient: 34); // hot summer day
        var r = fp.Evaluate(T0.AddDays(1).AddHours(2));
        Assert.Equal(CoolingState.Healthy, r.State);
    }

    [Fact]
    public void Dust_buildup_is_detected_and_forecast()
    {
        var fp = new CoolingFingerprint();
        for (int w = 0; w < CoolingFingerprint.BaselineObservations; w++) FeedWindow(fp, T0.AddMinutes(w * 6), 0.40);
        CoolingReport? worsened = null;
        fp.StateWorsened += r => worsened = r;
        // Thermal resistance creeps up ~0.6 % per day for 3 weeks (filters clogging).
        for (int day = 1; day <= 21; day++)
            for (int w = 0; w < 3; w++)
                FeedWindow(fp, T0.AddDays(day).AddMinutes(w * 6), 0.40 * (1 + 0.006 * day));
        var report = fp.Evaluate(T0.AddDays(21).AddHours(1));
        Assert.True(report.EfficiencyPercent < 92);
        Assert.True(report.TrendPercentPerWeek < 0);
        Assert.NotEqual(CoolingState.Healthy, report.State);
        Assert.NotNull(report.DaysUntilCleaning);
        Assert.NotNull(worsened);
    }

    [Fact]
    public void Idle_windows_without_power_swing_teach_nothing()
    {
        var fp = new CoolingFingerprint();
        for (int s = 0; s < 3600; s++) fp.Record(T0.AddSeconds(s), 40, 18, 900);
        Assert.Equal(0, fp.Evaluate(T0.AddHours(1)).Observations);
    }

    [Fact]
    public void Fan_that_slows_at_the_same_duty_is_flagged()
    {
        var fp = new CoolingFingerprint();
        FanChannelSnapshot Fan(double rpm) => new("fan1", "Rear fan", rpm, 60, false, FanDeviceKind.Fan, "", "SuperIO", 1);
        for (int i = 0; i < CoolingFingerprint.FanBaselineSamples; i++) fp.Record(T0.AddSeconds(i), null, null, null, new[] { Fan(1200) });
        for (int i = 0; i < CoolingFingerprint.FanRecentSamples; i++) fp.Record(T0.AddDays(30).AddSeconds(i), null, null, null, new[] { Fan(950) });
        var r = fp.Evaluate(T0.AddDays(30).AddMinutes(2));
        var w = Assert.Single(r.FanWarnings);
        Assert.Equal("Rear fan", w.FanName);
        Assert.InRange(w.DropPercent, 20, 22);
    }

    [Fact]
    public void Fingerprint_survives_save_and_load()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fanzi-fp-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "fp.json");
        try
        {
            var fp = new CoolingFingerprint();
            for (int w = 0; w < CoolingFingerprint.BaselineObservations; w++) FeedWindow(fp, T0.AddMinutes(w * 6), 0.35);
            fp.Save(path);
            var loaded = CoolingFingerprint.Load(path, T0.AddHours(2));
            Assert.Equal(CoolingState.Healthy, loaded.LastReport.State);
            File.WriteAllText(path, "{ broken");
            Assert.Equal(CoolingState.Learning, CoolingFingerprint.Load(path, T0).LastReport.State);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Doctor_signatures_group_numbers_and_redact_secrets()
    {
        Assert.Equal(SelfDoctor.Signature("frame 1234 failed on device 3"), SelfDoctor.Signature("frame 99 failed on device 7"));
        string red = SelfDoctor.Redact("{\"NotificationEmail\":\"me@x.com\",\"GoogleRefreshToken\":\"abc\",\"PollingIntervalSeconds\":1}");
        Assert.DoesNotContain("me@x.com", red);
        Assert.DoesNotContain("abc", red);
        Assert.Contains("PollingIntervalSeconds", red);
    }

    [Fact]
    public async Task Doctor_repairs_missing_sensors_once_per_cooldown()
    {
        var now = T0;
        int recoveries = 0;
        var snap = new HardwareSnapshot(now, null, null, null, 5, null, null, null, null, null, null, null, null, null,
            Array.Empty<CpuReadingSnapshot>(), Array.Empty<GpuReadingSnapshot>(), Array.Empty<FanChannelSnapshot>(), "", null, null, null, null);
        using var doctor = new SelfDoctor(new SelfDoctorContext
        {
            LastSnapshot = () => snap with { Timestamp = now },
            RecoverSensors = () => { recoveries++; return Task.FromResult(true); },
            WorkingSetBytes = () => 200L * 1024 * 1024,
        }, () => now);

        var first = await doctor.RunAsync();
        Assert.Equal(DoctorStatus.Ok, first.Single(c => c.Name == "Sensors").Status); // one missing reading: watch
        now = now.AddSeconds(30);
        var second = await doctor.RunAsync();
        Assert.Equal(DoctorStatus.Fixed, second.Single(c => c.Name == "Sensors").Status);
        Assert.Equal(1, recoveries);
        Assert.Equal(1, doctor.FixesApplied);
    }

    [Fact]
    public async Task Doctor_flags_a_fan_that_stopped_while_driven()
    {
        var now = T0;
        double rpm = 1100;
        using var doctor = new SelfDoctor(new SelfDoctorContext
        {
            LastSnapshot = () => new HardwareSnapshot(now, 50, 50, 55, 20, null, 40, null, null, null, null, null, null, null,
                Array.Empty<CpuReadingSnapshot>(), Array.Empty<GpuReadingSnapshot>(),
                new[] { new FanChannelSnapshot("f", "Front fan", rpm, 60, true, FanDeviceKind.Fan, "", "SuperIO", 0) }, "", null, null, null, null),
            WorkingSetBytes = () => 1,
        }, () => now);
        Assert.Equal(DoctorStatus.Ok, (await doctor.RunAsync()).Single(c => c.Name == "Fans").Status);
        rpm = 0;
        Assert.Equal(DoctorStatus.Attention, (await doctor.RunAsync()).Single(c => c.Name == "Fans").Status);
    }

    [Fact]
    public async Task Doctor_detects_an_error_storm()
    {
        var now = T0;
        using var doctor = new SelfDoctor(new SelfDoctorContext { WorkingSetBytes = () => 1 }, () => now);
        for (int i = 0; i < SelfDoctor.StormThreshold + 2; i++) AppLog.Warn($"OpenRGB: frame {i} failed (test storm)");
        var check = (await doctor.RunAsync()).Single(c => c.Name == "Error log");
        Assert.Equal(DoctorStatus.Attention, check.Status);
        if (doctor.LastBundlePath is { } bundle) { Assert.True(File.Exists(bundle)); File.Delete(bundle); }
    }

    [Theory]
    [InlineData("SERVICE_NAME: MaxsunSyncService\r\n        TYPE               : 10  WIN32_OWN_PROCESS\r\n        STATE              : 4  RUNNING\r\n", "RUNNING")]
    [InlineData("        STATE              : 1  STOPPED \r\n", "STOPPED")]
    [InlineData("[SC] EnumQueryServicesStatus:OpenService FAILED 1060:", null)]
    public void Service_state_is_parsed(string output, string? expected) =>
        Assert.Equal(expected, MainWindowViewModel.ParseScState(output));
}
