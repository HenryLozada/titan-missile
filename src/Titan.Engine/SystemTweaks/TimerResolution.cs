using Microsoft.Win32;
using static Titan.Engine.Native.NativeMethods;

namespace Titan.Engine.SystemTweaks;

/// <summary>
/// Kernel timer resolution for the session. Held by TITAN's own process, so Windows drops it by itself
/// if we exit or crash — nothing to journal.
///
/// Since Windows 10 2004 a request only affects the process that makes it, unless Windows 11's
/// GlobalTimerResolutionRequests is on (the "Resolución de temporizador global" tweak). Without that,
/// raising it from TITAN would be a placebo, so we don't.
/// </summary>
public static class TimerResolution
{
    private const string KernelKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel";
    private static uint _held;

    /// <summary>Whether a request from TITAN reaches the game on this Windows.</summary>
    public static bool Effective
    {
        get
        {
            if (Environment.OSVersion.Version.Build < 19041) return true;
            using var k = Registry.LocalMachine.OpenSubKey(KernelKey);
            return k?.GetValue("GlobalTimerResolutionRequests") is int v && v == 1;
        }
    }

    /// <summary>Requests the finest resolution (0.5 ms on most PCs). Returns the resolution in ms, or null if not applied.</summary>
    public static double? Raise()
    {
        if (_held != 0) return _held / 10_000.0;
        if (!Effective) return null;
        if (NtQueryTimerResolution(out _, out uint finest, out _) != STATUS_SUCCESS) return null;
        uint target = Math.Max(finest, 5_000);
        if (NtSetTimerResolution(target, true, out uint current) != STATUS_SUCCESS) return null;
        _held = target;
        return current / 10_000.0;
    }

    public static void Release()
    {
        if (_held == 0) return;
        NtSetTimerResolution(_held, false, out _);
        _held = 0;
    }
}
