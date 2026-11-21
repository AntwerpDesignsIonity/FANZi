using Fanzi.FanControl.AI;
using Fanzi.FanControl.Models;
using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.Tests;

/// <summary>4.6: anomaly rules that match real faults (field log: ~350 false alerts a day on a healthy PC).</summary>
public sealed class V46AnomalyDetectorTests
{
    private static AnomalyDetector Warm(double tempC, double? rpm, int samples = 30)
    {
        var d = new AnomalyDetector();
        for (int i = 0; i < samples; i++) d.AddSample(tempC + (i % 2 == 0 ? 0.2 : -0.2), rpm);
        return d;
    }

    [Fact]
    public void SteadySystem_SmallStep_IsNotASpike()
    {
        // Old rule: σ ≈ 0.2 °C, so +1 °C was "2.5σ above the mean" and raised an alert.
        var d = Warm(45, 900);
        for (int i = 0; i < 10; i++) d.AddSample(46.5, 900);
        Assert.Null(d.LastReport);
    }

    [Fact]
    public void OrdinaryLoadStep_BelowSpikeFloor_IsNotASpike()
    {
        var d = Warm(40, 900);
        for (int i = 0; i < 10; i++) d.AddSample(62, 1400); // app start: +22 °C, fan follows — normal
        Assert.Null(d.LastReport);
    }

    [Fact]
    public void LargeJumpWhileHot_IsReportedAfterThreeSamples()
    {
        var d = Warm(55, 1000);
        d.AddSample(86, 1000);
        Assert.Null(d.LastReport);
        d.AddSample(86, 1000);
        Assert.Null(d.LastReport);
        d.AddSample(86, 1000);

        Assert.NotNull(d.LastReport);
        Assert.Equal(AnomalySeverity.Warning, d.LastReport!.Severity);
        Assert.StartsWith("Temperature spike", d.LastReport.Anomalies[0]);
    }

    [Fact]
    public void CriticalTemperature_IsImmediate_EvenWithoutBaseline()
    {
        var d = new AnomalyDetector();
        d.AddSample(97, 1500);
        Assert.Equal(AnomalySeverity.Critical, d.LastReport?.Severity);
    }

    [Fact]
    public void FanSpeedChangingWithLoad_IsNotReported()
    {
        var d = Warm(50, 800);
        foreach (double rpm in new[] { 1200.0, 1500, 1700, 1100, 800, 1600 })
            for (int i = 0; i < 6; i++) d.AddSample(60, rpm);
        Assert.Null(d.LastReport);
    }

    [Fact]
    public void FanStall_NeedsThreeSamples_ThenIsCritical()
    {
        var d = Warm(50, 1200);
        d.AddSample(50, 0);
        d.AddSample(50, 0);
        Assert.Null(d.LastReport); // one or two zero readings are a Super IO glitch
        d.AddSample(50, 0);

        Assert.Equal(AnomalySeverity.Critical, d.LastReport?.Severity);
        Assert.StartsWith("Fan stall", d.LastReport!.Anomalies[0]);
    }

    [Fact]
    public void FanStoppedOnPurpose_ZeroRpmCurve_IsNotAStall()
    {
        // Zero-RPM preset: duty 0 % ⇒ 0 RPM is the intent. It used to raise "Fan stall", which pinned the fan at 100 %.
        var d = Warm(45, 1200);
        for (int i = 0; i < 10; i++) d.AddSample(45, 0, commandedDutyPercent: 0);
        Assert.Null(d.LastReport);
    }

    [Fact]
    public void Engine_ZeroRpmCurve_DoesNotJumpToFullSpeed()
    {
        var engine = new SmartFanCurveEngine();
        var curve = SmartFanCurveEngine.GenerateZeroRpmCurve();
        for (int i = 0; i < 30; i++) engine.ComputeOptimalFanSpeed(40, 5, 1100, curve, fanDutyPercent: 40);

        double result = 0;
        for (int i = 0; i < 10; i++) result = engine.ComputeOptimalFanSpeed(40, 5, 0, curve, fanDutyPercent: 0);

        Assert.True(result < 100, $"fan must not be forced to 100 % (was {result})");
        Assert.Null(engine.AnomalyDetector.LastReport);
    }

    [Fact]
    public void FanSlowedWhileHot_IsAWarning()
    {
        var d = Warm(72, 1600);
        for (int i = 0; i < AnomalyDetector.SlowFanPersistSamples; i++) d.AddSample(78, 600);
        Assert.Equal(AnomalySeverity.Warning, d.LastReport?.Severity);
        Assert.StartsWith("Fan slowed while hot", d.LastReport!.Anomalies[0]);
    }

    [Fact]
    public void NonFiniteReadings_AreIgnored()
    {
        var d = Warm(50, 1000);
        d.AddSample(double.NaN, double.PositiveInfinity);
        Assert.Null(d.LastReport);
    }
}

/// <summary>4.6 Game Mode: launchers are not games, the session clock runs, and a session ends.</summary>
public sealed class V46GameModeTests
{
    [Theory]
    [InlineData("steam")]
    [InlineData("steamwebhelper")]
    [InlineData("Battle.net")]
    [InlineData("EpicGamesLauncher")]
    [InlineData("RiotClientServices")]
    [InlineData("EADesktop")]
    [InlineData("UbisoftConnect")]
    [InlineData("GalaxyClient")]
    [InlineData("LeagueClient")]
    public void StoreLaunchers_AreNotGames(string process)
    {
        Assert.False(GameModeDetector.IsKnownGame(process));
        Assert.False(GameModeDetector.IsHeavyApp(process));
    }

    [Theory]
    [InlineData("cs2")]
    [InlineData("Cyberpunk2077")]
    [InlineData("FortniteClient-Win64-Shipping")]
    public void Games_AreStillGames(string process) => Assert.True(GameModeDetector.IsKnownGame(process));

    [Theory]
    [InlineData("javaw")]
    [InlineData("Blender")]
    [InlineData("obs64")]
    [InlineData("UnrealEditor")]
    public void EditorsAndCaptureTools_NeedGpuLoad(string process)
    {
        Assert.False(GameModeDetector.IsKnownGame(process));
        Assert.True(GameModeDetector.IsHeavyApp(process));
    }

    [Fact]
    public void RepeatedScans_DoNotRestartTheSessionClock()
    {
        var d = new GameModeDetector(() => (true, "cs2"));
        d.SetDetectedGame("cs2");
        Thread.Sleep(60);
        var first = d.GameModeDuration;
        d.SetDetectedGame("cs2"); // next scan, same game

        Assert.True(d.GameModeDuration >= first, "the session clock must keep running across scans");
        Assert.True(d.GameModeDuration.TotalMilliseconds >= 50);
    }

    [Fact]
    public void GameClosed_WithQuietGpu_EndsGameMode()
    {
        var d = new GameModeDetector(() => (false, ""));
        d.SetDetectedGame("cs2");
        for (int i = 0; i < 6; i++) d.Update(gpuLoadPercent: 5, cpuTempC: 58); // warm-idling CPU: old rule never exited
        d.SetNoGameRunning();

        Assert.False(d.IsGameModeActive);
        Assert.Equal("", d.DetectedGame);
    }

    [Fact]
    public void GameProcessGone_ButGpuStillBusy_KeepsCoolingWithoutTheName()
    {
        var d = new GameModeDetector(() => (false, ""));
        d.SetDetectedGame("cs2");
        for (int i = 0; i < 6; i++) d.Update(gpuLoadPercent: 85, cpuTempC: 75);
        d.SetNoGameRunning();

        Assert.True(d.IsGameModeActive);
        Assert.Equal("", d.DetectedGame);

        for (int i = 0; i < 40; i++) d.Update(gpuLoadPercent: 3, cpuTempC: 60);
        Assert.False(d.IsGameModeActive); // load-triggered sessions end on GPU load alone
    }

    [Fact]
    public void NoGame_WhenNothingWasDetected_ChangesNothing()
    {
        var d = new GameModeDetector(() => (false, ""));
        d.SetNoGameRunning();
        Assert.False(d.IsGameModeActive);
    }
}

/// <summary>4.6 AEDi Autopilot decision logic.</summary>
public sealed class V46AutopilotBrainTests
{
    private static readonly DateTime Noon = new(2026, 11, 21, 12, 0, 0);
    private static readonly DateTime Night = new(2026, 11, 21, 23, 30, 0);

    private static AutopilotInputs In(DateTime t, double cpu = 45, double? gpu = 40, double cpuLoad = 5, double gpuLoad = 3, bool game = false, string name = "")
        => new(cpu, gpu, cpuLoad, gpuLoad, game, name, t);

    [Fact]
    public void FirstDecision_IsTakenAtOnce()
    {
        var brain = new AutopilotBrain();
        var d = brain.Decide(In(Noon));
        Assert.Equal(AutopilotScene.Idle, d.Scene);
        Assert.Equal("Silent", d.CurveName);
    }

    [Fact]
    public void SceneChange_WaitsForTheDwell()
    {
        var brain = new AutopilotBrain();
        brain.Decide(In(Noon));

        var early = brain.Decide(In(Noon.AddSeconds(5), game: true, name: "cs2"));
        Assert.Equal(AutopilotScene.Idle, early.Scene);

        var later = brain.Decide(In(Noon.AddSeconds(5) + AutopilotBrain.SceneDwell, game: true, name: "cs2"));
        Assert.Equal(AutopilotScene.Gaming, later.Scene);
        Assert.Equal("Gaming", later.CurveName);
        Assert.Equal("Gaming — cs2", later.Headline);
    }

    [Fact]
    public void FlickeringLoad_DoesNotSwitchScene()
    {
        var brain = new AutopilotBrain();
        brain.Decide(In(Noon));
        var t = Noon;
        for (int i = 0; i < 60; i++)
        {
            t = t.AddSeconds(1);
            bool game = i % 10 < 3; // 3 s on, 7 s off — never holds for the dwell
            Assert.Equal(AutopilotScene.Idle, brain.Decide(In(t, game: game)).Scene);
        }
    }

    [Fact]
    public void Protect_IsImmediate_AndHoldsUntilCooled()
    {
        var brain = new AutopilotBrain { Goal = AutopilotGoal.Quiet };
        brain.Decide(In(Noon));

        var hot = brain.Decide(In(Noon.AddSeconds(1), cpu: 92));
        Assert.Equal(AutopilotScene.Protect, hot.Scene);
        Assert.Equal(100, hot.SystemFanPercent);
        Assert.Equal("Performance", hot.CurveName); // even with the Quiet goal

        var stillWarm = brain.Decide(In(Noon.AddSeconds(2), cpu: 86));
        Assert.Equal(AutopilotScene.Protect, stillWarm.Scene);

        var cooled = brain.Decide(In(Noon.AddSeconds(3), cpu: AutopilotBrain.ProtectCpuTempC - AutopilotBrain.ProtectReleaseDeltaC - 1));
        Assert.NotEqual(AutopilotScene.Protect, cooled.Scene);
    }

    [Fact]
    public void HotGpu_AlsoProtects()
    {
        var brain = new AutopilotBrain();
        var d = brain.Decide(In(Noon, cpu: 60, gpu: 89));
        Assert.Equal(AutopilotScene.Protect, d.Scene);
        Assert.Contains("GPU at 89", d.Reason);
    }

    [Fact]
    public void QuietHours_UseSilent_UnlessDisabledOrGaming()
    {
        var brain = new AutopilotBrain();
        Assert.Equal(AutopilotScene.QuietHours, brain.Decide(In(Night, cpu: 60, cpuLoad: 30)).Scene);

        var off = new AutopilotBrain { QuietHoursEnabled = false };
        Assert.Equal(AutopilotScene.Everyday, off.Decide(In(Night, cpu: 60, cpuLoad: 30)).Scene);

        var gaming = new AutopilotBrain();
        Assert.Equal(AutopilotScene.Gaming, gaming.Decide(In(Night, game: true)).Scene);
    }

    [Theory]
    [InlineData(21, false)]
    [InlineData(22, true)]
    [InlineData(3, true)]
    [InlineData(6, true)]
    [InlineData(7, false)]
    public void QuietHourWindow(int hour, bool quiet)
        => Assert.Equal(quiet, AutopilotBrain.IsQuietHour(new DateTime(2026, 11, 21, hour, 15, 0)));

    [Fact]
    public void HeavyWork_NeedsSustainedLoad()
    {
        var brain = new AutopilotBrain();
        var t = Noon;
        AutopilotDecision d = brain.Decide(In(t));
        for (int i = 0; i < 90; i++)
        {
            t = t.AddSeconds(1);
            d = brain.Decide(In(t, cpu: 72, cpuLoad: 100));
        }
        Assert.Equal(AutopilotScene.HeavyWork, d.Scene);
        Assert.Equal("Workstation", d.CurveName);
    }

    [Theory]
    [InlineData(AutopilotGoal.Quiet, AutopilotScene.Everyday, "Silent")]
    [InlineData(AutopilotGoal.Quiet, AutopilotScene.Gaming, "Balanced")]
    [InlineData(AutopilotGoal.Balanced, AutopilotScene.Everyday, "Balanced")]
    [InlineData(AutopilotGoal.Balanced, AutopilotScene.Idle, "Silent")]
    [InlineData(AutopilotGoal.Balanced, AutopilotScene.HeavyWork, "Workstation")]
    [InlineData(AutopilotGoal.Cool, AutopilotScene.Gaming, "Performance")]
    [InlineData(AutopilotGoal.Cool, AutopilotScene.Idle, "Balanced")]
    [InlineData(AutopilotGoal.Cool, AutopilotScene.Protect, "Overclock")]
    [InlineData(AutopilotGoal.Balanced, AutopilotScene.Protect, "Performance")]
    public void CurveForGoalAndScene(AutopilotGoal goal, AutopilotScene scene, string expected)
    {
        var (name, curve) = AutopilotBrain.CurveFor(goal, scene);
        Assert.Equal(expected, name);
        Assert.True(curve.Count >= 2);
    }

    [Fact]
    public void EveryCurve_EndsAtFullSpeed_AndNeverStopsTheFan()
    {
        foreach (AutopilotGoal goal in Enum.GetValues<AutopilotGoal>())
        foreach (AutopilotScene scene in Enum.GetValues<AutopilotScene>())
        {
            var (name, curve) = AutopilotBrain.CurveFor(goal, scene);
            Assert.True(curve.Max(p => p.FanPercent) >= 100, $"{name} must reach 100 %");
            Assert.True(curve.Min(p => p.FanPercent) >= 20, $"{name} must never stop a fan (Autopilot does not use Zero-RPM)");
        }
    }

    [Fact]
    public void SystemTarget_NeverBelowMinimum_AndFollowsTheHotterPart()
    {
        var curve = SmartFanCurveEngine.GenerateSilentCurve();
        Assert.Equal(AutopilotBrain.MinSystemFanPercent, AutopilotBrain.SystemTarget(curve, 30, 30, AutopilotScene.Idle));

        double cpuOnly = AutopilotBrain.SystemTarget(curve, 60, 40, AutopilotScene.Everyday);
        double gpuHot = AutopilotBrain.SystemTarget(curve, 60, 86, AutopilotScene.Everyday);
        Assert.True(gpuHot > cpuOnly, "a hot GPU must raise case airflow even when the CPU is cool");

        Assert.Equal(100, AutopilotBrain.SystemTarget(curve, 50, 50, AutopilotScene.Protect));
        Assert.Equal(50, AutopilotBrain.SystemTarget(curve, null, null, AutopilotScene.Everyday)); // no sensors: safe middle
    }

    [Fact]
    public void SystemFanDuty_IsSlewLimited()
    {
        var brain = new AutopilotBrain { Goal = AutopilotGoal.Cool };
        double first = brain.Decide(In(Noon, cpu: 35, gpu: 35)).SystemFanPercent;
        double next = brain.Decide(In(Noon.AddSeconds(1), cpu: 80, gpu: 80)).SystemFanPercent;

        Assert.True(next - first <= 5.0 + 1e-6, $"at most +5 % per second (was {next - first:F1})");
        Assert.True(next > first);
    }

    [Fact]
    public void Reset_StartsOver()
    {
        var brain = new AutopilotBrain();
        brain.Decide(In(Noon, cpu: 92));
        brain.Reset();
        Assert.Equal(AutopilotScene.Idle, brain.Decide(In(Noon.AddSeconds(1))).Scene);
    }
}

/// <summary>4.6: settings that used to be lost or could break loading.</summary>
public sealed class V46SettingsTests
{
    [Fact]
    public void Defaults_AutopilotOff_QuietHoursOn_Balanced()
    {
        var s = new AppSettings();
        Assert.False(s.AutopilotEnabled); // an upgrade never starts driving fans by itself
        Assert.True(s.AutopilotQuietHours);
        Assert.Equal("Balanced", s.AutopilotGoal);
        Assert.Equal(587, s.SmtpPort);
        Assert.Null(s.SmtpPasswordProtected);
    }

    [Fact]
    public void Normalize_RepairsAutopilotGoal_SmtpPort_AndNullProfileCollections()
    {
        var s = new AppSettings { AutopilotGoal = "Turbo", SmtpPort = 0, SmtpHost = null!, SmtpUser = null! };
        s.Profiles.Add(new FanProfile { Name = " ", FanCurve = null!, FanChannelPercents = null!, NotificationEmail = null! });

        s.Normalize();

        Assert.Equal("Balanced", s.AutopilotGoal);
        Assert.Equal(587, s.SmtpPort);
        Assert.Equal("", s.SmtpHost);
        Assert.Equal("", s.SmtpUser);
        Assert.NotNull(s.Profiles[0].FanCurve);
        Assert.NotNull(s.Profiles[0].FanChannelPercents);
        Assert.Equal("", s.Profiles[0].NotificationEmail);
        Assert.Equal("Profile", s.Profiles[0].Name);
    }

    [Fact]
    public void Settings_RoundTrip_KeepsAutopilotAndSmtp()
    {
        var s = new AppSettings { AutopilotEnabled = true, AutopilotGoal = "Cool", AutopilotQuietHours = false, SmtpHost = "smtp.example.com", SmtpPort = 465, SmtpUser = "me@example.com", SmtpPasswordProtected = "dpapi:AAAA" };
        string json = System.Text.Json.JsonSerializer.Serialize(s);
        var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.True(back.AutopilotEnabled);
        Assert.Equal("Cool", back.AutopilotGoal);
        Assert.False(back.AutopilotQuietHours);
        Assert.Equal("smtp.example.com", back.SmtpHost);
        Assert.Equal(465, back.SmtpPort);
        Assert.Equal("dpapi:AAAA", back.SmtpPasswordProtected);
        Assert.DoesNotContain("SmtpPassword\"", json); // only the protected form exists
    }

    [Fact]
    public void SecretProtector_RoundTrips_AndNeverStoresClearText()
    {
        const string secret = "p@ss wörd-✓ 123";
        string? stored = SecretProtector.Protect(secret);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Null(stored); // no DPAPI: session only
            return;
        }

        Assert.NotNull(stored);
        Assert.StartsWith(SecretProtector.Prefix, stored);
        Assert.DoesNotContain("p@ss", stored);
        Assert.Equal(secret, SecretProtector.Unprotect(stored));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("plain-text-from-a-hand-edit")]
    [InlineData("dpapi:not-base64!!")]
    [InlineData("dpapi:AAAA")]
    public void SecretProtector_UnreadableValues_GiveEmpty(string? stored)
        => Assert.Equal("", SecretProtector.Unprotect(stored));

    [Fact]
    public void SecretProtector_EmptySecret_IsNotStored() => Assert.Null(SecretProtector.Protect(""));
}

/// <summary>4.6: a fatal crash hands the fans back before the process dies.</summary>
[Collection("AppLog")]
public sealed class V46CrashFanReleaseTests : IDisposable
{
    private readonly TempAppLog _log = new();

    public void Dispose()
    {
        CrashGuardService.EmergencyFanRelease = null;
        _log.Dispose();
    }

    [Fact]
    public void EmergencyRelease_RunsOnce_AndIsLogged()
    {
        int calls = 0;
        CrashGuardService.EmergencyFanRelease = () => calls++;

        CrashGuardService.InvokeEmergencyFanRelease();

        Assert.Equal(1, calls);
        Assert.Contains("fans handed back", _log.ReadAll());
    }

    [Fact]
    public void EmergencyRelease_ThatThrows_DoesNotThrow()
    {
        CrashGuardService.EmergencyFanRelease = () => throw new InvalidOperationException("driver gone");
        CrashGuardService.InvokeEmergencyFanRelease();
        Assert.Contains("emergency fan hand-back failed", _log.ReadAll());
    }

    [Fact]
    public void EmergencyRelease_NotRegistered_IsANoOp()
    {
        CrashGuardService.EmergencyFanRelease = null;
        CrashGuardService.InvokeEmergencyFanRelease();
        Assert.Equal("", _log.ReadAll());
    }
}

/// <summary>4.6 "Sign in with Google" out of the box: the Desktop client compiled into official builds.</summary>
public sealed class V46GoogleBuiltInClientTests
{
    private const string ClientJson =
        """{"installed":{"client_id":"123-abc.apps.googleusercontent.com","client_secret":"not-a-real-secret","redirect_uris":["http://localhost"]}}""";

    [Fact]
    public void ParseStream_ReadsGooglesDownloadedJson()
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(ClientJson));
        var config = GoogleOAuthConfig.ParseStream(stream, GoogleOAuthConfig.EmbeddedSource);

        Assert.NotNull(config);
        Assert.True(config!.HasSecret);
        Assert.Equal("123-abc.apps.googleusercontent.com", config.ClientId);
        Assert.Equal("(built in)", config.SourcePath);
    }

    [Fact]
    public void AssemblyWithoutTheResource_HasNoBuiltInClient()
        => Assert.Null(GoogleOAuthConfig.LoadEmbedded(typeof(V46GoogleBuiltInClientTests).Assembly));

    [Fact]
    public void ExplicitPathList_NeverFallsBackToTheBuiltInClient()
    {
        string missing = Path.Combine(Path.GetTempPath(), "fanzi-no-such-" + Guid.NewGuid().ToString("N") + ".json");
        var config = GoogleOAuthConfig.Load(new[] { missing });

        Assert.False(config.HasSecret);
        Assert.Equal(GoogleOAuthConfig.DefaultClientId, config.ClientId);
    }

    [Fact]
    public void BuiltInClient_WhenPresent_IsTheFanziDesktopClient()
    {
        // Present in builds made with google_oauth.json next to FANZI.slnx (official builds); absent on a bare clone / CI.
        var builtIn = GoogleOAuthConfig.LoadEmbedded(typeof(GoogleOAuthConfig).Assembly);
        if (builtIn is null) return;

        Assert.True(builtIn.HasSecret);
        Assert.Equal(GoogleOAuthConfig.DefaultClientId, builtIn.ClientId);
    }

    [Fact]
    public void SidebarCollapsed_DefaultsToOpen_AndRoundTrips()
    {
        Assert.False(new AppSettings().SidebarCollapsed);
        var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            System.Text.Json.JsonSerializer.Serialize(new AppSettings { SidebarCollapsed = true }))!;
        Assert.True(back.SidebarCollapsed);
    }
}

/// <summary>4.6 repository checks.</summary>
public sealed class V46RepositoryTests
{
    private static string? FindRepoFile(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, name);
            if (File.Exists(candidate) && File.Exists(Path.Combine(dir.FullName, "FANZI.slnx"))) return candidate;
        }
        return null;
    }

    [Fact]
    public void BuildScript_IsAsciiOnly_SoWindowsPowerShell51CanParseIt()
    {
        string? path = FindRepoFile("build.ps1");
        if (path is null) return; // test binaries copied elsewhere

        byte[] bytes = File.ReadAllBytes(path);
        int index = Array.FindIndex(bytes, b => b > 127);
        Assert.True(index < 0, $"build.ps1 has a non-ASCII byte at offset {index}: PowerShell 5.1 reads it as ANSI and a UTF-8 dash becomes a closing quote.");
    }

    [Fact]
    public void AediKnowsAutopilot()
    {
        var aedi = new AediKnowledgeEngine();
        var hit = aedi.Ping("autopilot");
        Assert.NotNull(hit);
        Assert.Equal(0, hit!.TargetTab); // Dashboard
        Assert.Contains(aedi.Search("autopilot"), r => r.Entry.Title == "Autopilot");
    }

    /// <summary>Tab order: Dashboard 0 … Tools 8, Bench 9, Alerts 10, Settings 11.</summary>
    [Theory]
    [InlineData("dashboard", 0)]
    [InlineData("fans", 1)]
    [InlineData("sensors", 2)]
    [InlineData("rgb", 3)]
    [InlineData("tasks", 4)]
    [InlineData("network", 5)]
    [InlineData("power", 6)]
    [InlineData("clean", 7)]
    [InlineData("tools", 8)]
    [InlineData("bench", 9)]
    [InlineData("alert", 10)]
    [InlineData("settings", 11)]
    [InlineData("overlay", 11)]
    [InlineData("about", 11)]
    [InlineData("help", 0)]
    public void AediQuickCommands_OpenTheRightTab(string command, int expectedTab)
    {
        var hit = new AediKnowledgeEngine().Ping(command);
        Assert.NotNull(hit);
        Assert.Equal(expectedTab, hit!.TargetTab);
    }

    [Fact]
    public void AediSettingsTab_MatchesTheViewModel()
        => Assert.Equal(Fanzi.FanControl.ViewModels.MainWindowViewModel.SettingsTabIndex, AediKnowledgeEngine.SettingsTab);
}
