using System.Threading;

namespace Fanzi.FanControl.Controls;

/// <summary>
/// App-wide "Reduce motion" switch (Settings → Appearance &amp; accessibility, persisted as
/// <c>AppSettings.ReduceMotion</c>). When on, gauge arcs jump instead of sweeping, big numbers change
/// without counting (<see cref="AnimatedNumber"/>) and the theme's looping loader / "now"-dot animations
/// are switched off (windows carry the <c>reduce-motion</c> class).
/// </summary>
public static class MotionPreferences
{
    private static int _reduceMotion;

    public static bool ReduceMotion
    {
        get => Volatile.Read(ref _reduceMotion) != 0;
        set => Volatile.Write(ref _reduceMotion, value ? 1 : 0);
    }
}
