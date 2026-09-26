using System.Diagnostics;
using static Titan.Engine.Native.NativeMethods;

namespace Titan.Engine.SystemTweaks;

public static unsafe class PowerPlans
{
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid UltimateTemplate = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    /// <summary>Our own copy of Ultimate Performance. Fixed GUID so it is created once, never duplicated again.</summary>
    public static readonly Guid TitanUltimate = new("7174616e-0000-4c74-8d69-6d6174650001");

    /// <summary>"Power mode" slider overlays (only meaningful while Balanced is active, e.g. Modern Standby laptops).</summary>
    public static readonly Guid OverlayBestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    public static Guid GetActive()
    {
        Guid* g = null;
        uint rc = PowerGetActiveScheme(0, &g);
        if (rc != 0 || g == null) throw new System.ComponentModel.Win32Exception((int)rc, "PowerGetActiveScheme");
        try { return *g; }
        finally { LocalFree(g); }
    }

    public static bool TrySetActive(Guid scheme) => PowerSetActiveScheme(0, scheme) == 0;

    public static bool Exists(Guid scheme)
    {
        uint size = 0;
        return PowerReadFriendlyName(0, scheme, null, null, null, ref size) == 0;
    }

    public static string? FriendlyName(Guid scheme)
    {
        uint size = 0;
        if (PowerReadFriendlyName(0, scheme, null, null, null, ref size) != 0 || size == 0) return null;
        var buf = new byte[size];
        fixed (byte* p = buf)
        {
            if (PowerReadFriendlyName(0, scheme, null, null, p, ref size) != 0) return null;
        }
        return System.Text.Encoding.Unicode.GetString(buf).TrimEnd('\0');
    }

    /// <summary>Creates "TITAN Ultimate" from the hidden Ultimate Performance template, once.</summary>
    public static bool EnsureTitanUltimate()
    {
        if (Exists(TitanUltimate)) return true;
        if (!RunPowercfg("-duplicatescheme", UltimateTemplate.ToString(), TitanUltimate.ToString())) return false;
        RunPowercfg("-changename", TitanUltimate.ToString(), "TITAN Ultimate", "Plan de maximo rendimiento creado por TITAN.");
        return Exists(TitanUltimate);
    }

    public static Guid? TryGetOverlay()
    {
        try { return PowerGetEffectiveOverlayScheme(out var g) == 0 ? g : null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    public static bool TrySetOverlay(Guid overlay)
    {
        try { return PowerSetActiveOverlayScheme(overlay) == 0; }
        catch (EntryPointNotFoundException) { return false; }
    }

    private static bool RunPowercfg(params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "powercfg.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit(10_000);
        return p.HasExited && p.ExitCode == 0;
    }
}
