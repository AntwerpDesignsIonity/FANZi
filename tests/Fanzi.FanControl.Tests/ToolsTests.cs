using Fanzi.FanControl.Services.Tools;
using System.Text;

namespace Fanzi.FanControl.Tests;

/// <summary>v4.1.0 Tools tab: tool runner (progress, encoding, errors, cancel), disk optimiser, drive health, power plans.</summary>
public class ToolRunnerTests
{
    [Theory]
    [InlineData("Analysis:  23% complete...", 23)]
    [InlineData("Retrim:  100% complete.", 100)]
    [InlineData("Verification 45% complete.", 45)]
    [InlineData("[=====                      10.0%                           ]", 10)]
    [InlineData("[==========================100.0%==========================] The restore operation completed successfully.", 100)]
    [InlineData("Progress: 12034 of 50000 done; Stage:  24%; Total:  10%; ETA:   0:00:12 .", 10)]
    public void ParseProgress_ReadsEveryToolsProgressLine(string line, double expected)
        => Assert.Equal(expected, ToolProcessRunner.ParseProgress(line));

    [Theory]
    [InlineData("\tTotal fragmented space      = 3%")]
    [InlineData("Free space = 171.62 GB")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Pass 1: 120% complete")] // out of range
    public void ParseProgress_IgnoresReportLines(string? line)
        => Assert.Null(ToolProcessRunner.ParseProgress(line));

    [Fact]
    public void SplitConsole_TreatsCarriageReturnRedrawsAsProgress_AndCrLfAsLineEnds()
    {
        var pending = new StringBuilder();
        var lines = ToolProcessRunner.SplitConsole("Invoking analysis on (C:)...\r\n\r\n\tAnalysis:  10% complete.\r\tAnalysis:  55% complete.", pending);
        Assert.Equal(new[] { ("Invoking analysis on (C:)...", false), ("", false), ("\tAnalysis:  10% complete.", true) }, lines);
        Assert.Equal("\tAnalysis:  55% complete.", pending.ToString());

        // The CR of a CRLF split across two reads is still a line end, not a redraw.
        var more = ToolProcessRunner.SplitConsole("\r", pending);
        Assert.Empty(more);
        more = ToolProcessRunner.SplitConsole("\nThe operation completed successfully.\r\n", pending);
        Assert.Equal(new[] { ("\tAnalysis:  55% complete.", false), ("The operation completed successfully.", false) }, more);
        Assert.Equal(0, pending.Length);
    }

    [Fact]
    public void SplitConsole_HandlesBackspaceRedraws()
    {
        var pending = new StringBuilder();
        ToolProcessRunner.SplitConsole("Stage: 9%\b\b10%", pending);
        Assert.Equal("Stage: 10%", pending.ToString());
    }

    [Fact]
    public void Utf16Detection_FindsSfcOutput()
    {
        Assert.True(ToolProcessRunner.LooksLikeUtf16(Encoding.Unicode.GetBytes("Beginning system scan.")));
        Assert.False(ToolProcessRunner.LooksLikeUtf16(Encoding.ASCII.GetBytes("Invoking analysis on (C:)...")));
        Assert.False(ToolProcessRunner.LooksLikeUtf16(new byte[] { 0x41 }));
    }

    [Fact]
    public void FindError_CatchesFailuresThatExitWithZero()
    {
        Assert.NotNull(ToolProcessRunner.FindError("The storage optimiser cannot start because you have insufficient privileges to perform this operation. (0x89000024)"));
        Assert.NotNull(ToolProcessRunner.FindError("Error: 740\r\n\r\nElevated permissions are required to run DISM."));
        Assert.NotNull(ToolProcessRunner.FindError("System error 5 has occurred.\nAccess is denied."));
        Assert.Null(ToolProcessRunner.FindError("Windows Resource Protection did not find any integrity violations.\nThe operation completed successfully."));
        Assert.Null(ToolProcessRunner.FindError("Total fragmented space = 0%\nYou do not need to defragment this volume."));
    }

    [Fact]
    public async Task RunAsync_StreamsRealOutput()
    {
        if (!OperatingSystem.IsWindows()) return;
        var lines = new List<string>();
        var progress = new SyncProgress(p => { if (p.Line is { } l) lines.Add(l); });
        var result = await new ToolProcessRunner().RunAsync(new ToolCommand("Echo", "cmd.exe", "/c echo FANZi tools & echo second line"), progress, CancellationToken.None);

        Assert.True(result.Succeeded, result.ErrorSummary);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("FANZi tools", result.Output);
        Assert.Contains("second line", result.Output);
        Assert.Contains(lines, l => l.Contains("second line"));
    }

    [Fact]
    public async Task RunAsync_DecodesSfcUtf16()
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await new ToolProcessRunner().RunAsync(new ToolCommand("SFC help", "sfc.exe", "/?"), null, CancellationToken.None);
        // Elevated: the usage text; not elevated: "You must be an administrator…" — either way readable text, no NULs.
        Assert.True(result.Output.Contains("SCANNOW", StringComparison.OrdinalIgnoreCase)
                    || result.Output.Contains("administrator", StringComparison.OrdinalIgnoreCase), result.Output);
        Assert.False(result.Output.Contains('\0'), "NUL characters left in the decoded output");
        Assert.DoesNotContain("Y o u", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ReportsFailures_AndMissingTools()
    {
        if (!OperatingSystem.IsWindows()) return;
        var runner = new ToolProcessRunner();
        var failed = await runner.RunAsync(new ToolCommand("Exit 3", "cmd.exe", "/c exit 3"), null, CancellationToken.None);
        Assert.False(failed.Succeeded);
        Assert.Equal(3, failed.ExitCode);

        var missing = await runner.RunAsync(new ToolCommand("Nope", "fanzi-no-such-tool-xyz.exe", ""), null, CancellationToken.None);
        Assert.False(missing.Succeeded);
        Assert.Contains("could not start", missing.ErrorSummary);
    }

    [Fact]
    public async Task RunAsync_CancelKillsTheTool()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
        var started = DateTime.UtcNow;
        var result = await new ToolProcessRunner().RunAsync(new ToolCommand("Wait", "ping.exe", "-n 30 127.0.0.1"), null, cts.Token);
        Assert.True(result.Cancelled);
        Assert.False(result.Succeeded);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    /// <summary>Synchronous IProgress (Progress&lt;T&gt; posts to a context and could land after the assert).</summary>
    private sealed class SyncProgress(Action<ToolProgress> report) : IProgress<ToolProgress>
    {
        public void Report(ToolProgress value) { lock (this) report(value); }
    }
}

public class DiskToolsTests
{
    [Theory]
    [InlineData(DiskOperation.Analyze, "C: /A /U /V")]
    [InlineData(DiskOperation.Optimize, "D: /O /U /V")]
    [InlineData(DiskOperation.Defragment, "E: /D /U /V")]
    [InlineData(DiskOperation.Retrim, "F: /L /U /V")]
    [InlineData(DiskOperation.BootOptimize, "C: /B /U /V")]
    public void Commands_UseDefragWithProgress(DiskOperation op, string args)
    {
        char letter = args[0];
        var cmd = DiskToolsService.CommandFor(op, char.ToLowerInvariant(letter));
        Assert.Equal("defrag.exe", cmd.FileName);
        Assert.Equal(args, cmd.Arguments);
    }

    [Fact]
    public void Commands_RejectNonLetters()
        => Assert.Throws<ArgumentOutOfRangeException>(() => DiskToolsService.CommandFor(DiskOperation.Analyze, '1'));

    [Fact]
    public void ParseReport_ReadsFragmentationAndAdvice()
    {
        const string log = """
            Invoking analysis on (C:)...
            The operation completed successfully.
            Post Defragmentation Report:
            	Volume Information:
            		Volume size                 = 930.51 GB
            		Free space                  = 171.62 GB
            		Total fragmented space      = 7%
            		Largest free space size     = 168.40 GB
            	Note: File fragments larger than 64MB are not included in the fragmentation statistics.
            	It is recommended that you defragment this volume.
            """;
        var report = DiskToolsService.ParseReport(log);
        Assert.Equal(7, report.FragmentedPercent);
        Assert.Equal("It is recommended that you defragment this volume.", report.Recommendation);

        var none = DiskToolsService.ParseReport("The operation completed successfully.");
        Assert.Null(none.FragmentedPercent);
        Assert.Null(none.Recommendation);
    }

    [Fact]
    public void ParseVolumes_MapsMediaAndHandlesASingleObject()
    {
        const string json = """[{"L":"D","Label":"| Workspace |","Fs":"NTFS","Size":1000202039296,"Free":140169822208,"Disk":1,"Media":"SSD","Bus":"NVMe","Name":"KIOXIA"},{"L":"C","Label":"","Fs":"NTFS","Size":999038119936,"Free":184287940608,"Disk":0,"Media":"HDD","Bus":"SATA","Name":"WD"},{"L":"","Label":"x"}]""";
        var volumes = DiskToolsService.ParseVolumes(json);
        Assert.Equal(new[] { 'C', 'D' }, volumes.Select(v => v.Letter));
        Assert.Equal(DiskMedia.Hdd, volumes[0].Media);
        Assert.True(volumes[0].CanDefragment);
        Assert.False(volumes[1].CanDefragment);
        Assert.True(volumes[1].CanRetrim);
        Assert.InRange(volumes[1].UsedPercent, 85, 87);

        var single = DiskToolsService.ParseVolumes("""{"L":"F","Fs":"NTFS","Size":100,"Free":50,"Disk":null,"Media":"","Bus":"","Name":""}""");
        Assert.Single(single);
        Assert.Equal(DiskMedia.Unknown, single[0].Media);
        Assert.Null(single[0].DiskNumber);
        Assert.Empty(DiskToolsService.ParseVolumes(""));
    }

    [Fact]
    public void ParseHealth_FlagsWearErrorsHeat_AndExplainsMissingAdmin()
    {
        const string json = """
            [{"Id":"0","Name":"KINGSTON SNV3S1000G","Media":"SSD","Bus":"NVMe","Health":"Healthy","Op":"OK","Size":1000204886016,"Temp":41,"TempMax":70,"Wear":3,"ReadErr":0,"WriteErr":0,"Hours":1234,"Err":null},
             {"Id":"1","Name":"Old HDD","Media":"HDD","Bus":"SATA","Health":"Warning","Op":"OK","Size":500000000000,"Temp":67,"TempMax":0,"Wear":null,"ReadErr":12,"WriteErr":0,"Hours":40000,"Err":null},
             {"Id":"2","Name":"Samsung SSD 850 EVO 1TB","Media":"SSD","Bus":"USB","Health":"Healthy","Op":"OK","Size":1000204886016,"Temp":null,"TempMax":null,"Wear":null,"ReadErr":null,"WriteErr":null,"Hours":null,"Err":"Access to a CIM resource was not available to the client."}]
            """;
        var drives = DiskToolsService.ParseHealth(json);
        Assert.Equal(3, drives.Count);
        Assert.Equal("Healthy", drives[0].Verdict);
        Assert.Equal(41, drives[0].TemperatureC);
        Assert.Equal(1234, drives[0].PowerOnHours);

        Assert.Contains("Windows reports Warning", drives[1].Verdict);
        Assert.Contains("read/write errors", drives[1].Verdict);
        Assert.Contains("hot", drives[1].Verdict);
        Assert.Null(drives[1].TemperatureMaxC); // 0 = not reported

        Assert.Equal("Healthy", drives[2].Verdict);
        Assert.Contains("Administrator", drives[2].Note);
    }

    [Fact]
    public async Task ListVolumes_FindsTheSystemDrive()
    {
        if (!OperatingSystem.IsWindows()) return;
        var volumes = await new DiskToolsService().ListVolumesAsync();
        char system = char.ToUpperInvariant(Environment.GetFolderPath(Environment.SpecialFolder.Windows)[0]);
        Assert.Contains(volumes, v => v.Letter == system && v.SizeBytes > 0);
    }
}

public class SystemToolsTests
{
    [Fact]
    public void Catalogue_IsComplete_UniqueAndPointsAtRealTools()
    {
        var all = SystemToolsService.Catalogue;
        Assert.Equal(all.Count, all.Select(a => a.Id).Distinct().Count());
        foreach (var id in new[] { "restore-point", "sfc", "dism-check", "dism-scan", "dism-restore", "winsxs-cleanup", "chkdsk-scan",
                     "flush-dns", "renew-ip", "winsock-reset", "tcpip-reset", "time-sync", "restart-explorer", "icon-cache",
                     "restart-audio", "reset-wu", "memory-test" })
            Assert.Contains(all, a => a.Id == id);
        Assert.All(all, a =>
        {
            Assert.Contains(a.Group, new[] { "Repair", "Network", "Fixes" });
            Assert.False(string.IsNullOrWhiteSpace(a.Description));
        });
        // Disruptive ones ask first; restarts are flagged.
        Assert.NotNull(all.Single(a => a.Id == "renew-ip").Confirm);
        Assert.True(all.Single(a => a.Id == "winsock-reset").Command.NeedsRestart);
        Assert.Null(all.Single(a => a.Id == "sfc").Confirm);

        if (OperatingSystem.IsWindows())
            foreach (var exe in all.Select(a => a.Command.FileName).Distinct())
                Assert.True(File.Exists(Path.Combine(Environment.SystemDirectory, exe))
                            || File.Exists(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", exe)), $"{exe} not found");
    }

    [Fact]
    public void ExplorerRestart_NeverStartsAnElevatedShell()
    {
        var cmd = SystemToolsService.Catalogue.Single(a => a.Id == "restart-explorer").Command;
        Assert.DoesNotContain("start \"\" explorer", cmd.Arguments);
        Assert.Contains("runas /trustlevel:0x20000", cmd.Arguments);
    }

    [Fact]
    public void ParsePowerPlans_FindsActiveAndUltimate()
    {
        const string output = """

            Existing Power Schemes (* Active)
            -----------------------------------
            Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced) *
            Power Scheme GUID: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (High performance)
            Power Scheme GUID: a1841308-3541-4fab-bc81-f71556f20b4a  (Power saver)
            """;
        var plans = SystemToolsService.ParsePowerPlans(output);
        Assert.Equal(3, plans.Count);
        Assert.Equal("Balanced", plans.Single(p => p.IsActive).Name);
        Assert.False(SystemToolsService.HasUltimate(plans));
        Assert.True(SystemToolsService.HasUltimate(plans.Append(new PowerPlan(Guid.NewGuid(), "Ultimate Performance", false))));
        Assert.Equal("● Balanced", plans[0].Display);
        Assert.Equal("/setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", SystemToolsService.SetActiveCommand(plans[1].Id).Arguments);
        Assert.Equal("-duplicatescheme e9a42b02-d5df-448d-aa00-03f14749eb61", SystemToolsService.AddUltimateCommand.Arguments);
        Assert.Empty(SystemToolsService.ParsePowerPlans(""));
    }

    [Fact]
    public async Task PowerPlans_ReadFromThisPc()
    {
        if (!OperatingSystem.IsWindows()) return;
        var plans = await SystemToolsService.ListPowerPlansAsync(new ToolProcessRunner());
        Assert.NotEmpty(plans);
        Assert.Single(plans, p => p.IsActive);
    }
}
