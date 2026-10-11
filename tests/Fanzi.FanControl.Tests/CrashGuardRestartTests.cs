using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.Tests;

public sealed class CrashGuardRestartTests
{
    [Theory]
    [InlineData(new string[0], 0)]
    [InlineData(new[] { "--minimized" }, 0)]
    [InlineData(new[] { "--recovered", "--restart-count=1" }, 1)]
    [InlineData(new[] { "--restart-count=3", "--recovered" }, 3)]
    [InlineData(new[] { "--RESTART-COUNT=2" }, 2)]
    [InlineData(new[] { "--restart-count=" }, 0)]
    [InlineData(new[] { "--restart-count=abc" }, 0)]
    [InlineData(new[] { "--restart-count=-4" }, 0)]
    [InlineData(new[] { "--restart-count=99999999999" }, 0)]
    public void ParseRestartCount(string[] args, int expected)
    {
        Assert.Equal(expected, CrashGuardService.ParseRestartCount(args));
    }

    [Theory]
    [InlineData(new[] { "--recovered" }, true)]
    [InlineData(new[] { "--minimized", "--Recovered" }, true)]
    [InlineData(new[] { "--minimized" }, false)]
    [InlineData(new string[0], false)]
    public void IsRecoveredStart(string[] args, bool expected)
    {
        Assert.Equal(expected, CrashGuardService.IsRecoveredStart(args));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(7, false)]
    public void ShouldRestart_StopsAtThree(int count, bool expected)
    {
        Assert.Equal(expected, CrashGuardService.ShouldRestart(count));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(3, true)]
    public void RestartArguments_RoundTripThroughParser(int next, bool minimized)
    {
        string[] args = CrashGuardService.BuildRestartArguments(next, minimized)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        Assert.True(CrashGuardService.IsRecoveredStart(args));
        Assert.Equal(next, CrashGuardService.ParseRestartCount(args));
        Assert.Equal(minimized, args.Contains("--minimized"));
    }
}
