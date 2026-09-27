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

    private static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid SubUsb = new("2a737441-1930-4402-8d77-b2bebba308a3");
    private static readonly Guid SubPcie = new("501a4d13-42af-4429-9fd1-a8139f2c4b5b");
    private static readonly Guid SubDisk = new("0012ee47-9041-4b5d-9b77-535fba8b1442");

    /// <summary>What "maximum" means in our own scheme. Only ever written to TITAN Ultimate, never to the user's plans.</summary>
    private static readonly (Guid Sub, Guid Setting, uint Value, string What)[] MaxSettings =
    [
        (SubProcessor, new("893dee8e-2bef-41e0-89c6-b55d0929964c"), 100, "estado mínimo del procesador 100 %"),
        (SubProcessor, new("bc5038f7-23e0-4960-96da-33abaf5935ec"), 100, "estado máximo del procesador 100 %"),
        (SubProcessor, new("0cc5b647-c1df-4637-891a-dec35c318583"), 100, "sin aparcamiento de núcleos"),
        (SubProcessor, new("be337238-0d82-4146-a960-4f3749d470c7"), 2, "turbo agresivo"),
        (SubProcessor, new("36687f9e-e3a5-4dbf-b1dc-15eb381c6863"), 0, "preferencia energética: rendimiento"),
        (SubUsb, new("48e6b7a6-50f5-4782-a5d4-53bb8f07e226"), 0, "USB sin suspensión selectiva"),
        (SubPcie, new("ee12f906-d277-404b-b6da-e5fa1a576df5"), 0, "PCIe sin ahorro de enlace"),
        (SubDisk, new("6738e2c4-e8a5-4a42-b16a-e040e769756e"), 0, "discos siempre despiertos"),
    ];

    /// <summary>
    /// Pins TITAN Ultimate to full performance (idempotent). Settings a CPU doesn't expose are skipped.
    /// Returns how many were written.
    /// </summary>
    public static int TuneTitanUltimate()
    {
        if (!Exists(TitanUltimate)) return 0;
        int ok = 0;
        foreach (var (sub, setting, value, _) in MaxSettings)
        {
            bool ac = PowerWriteACValueIndex(0, TitanUltimate, sub, setting, value) == 0;
            bool dc = PowerWriteDCValueIndex(0, TitanUltimate, sub, setting, value) == 0;
            if (ac || dc) ok++;
        }
        if (GetActive() == TitanUltimate) TrySetActive(TitanUltimate);   // re-apply so the new values take effect now
        return ok;
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
