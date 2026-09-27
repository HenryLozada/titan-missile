using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using Titan.Engine.Session;

namespace Titan.Engine.SystemTweaks;

public enum TweakKind { Registry, MemoryCompression, Diagnostic }

public sealed record TweakDefinition(
    string Id, string Title, string Description, string Impact, bool RequiresRestart, TweakKind Kind,
    IReadOnlyList<DwordValue> Values)
{
    /// <summary>Values that must be pushed to the running system after writing the registry (mouse).</summary>
    public Action? AfterApply { get; init; }
}

public sealed record TweakStatus(TweakDefinition Definition, bool Active, bool Applied, string Detail);

public sealed record AppliedTweak(string Id, DateTimeOffset AppliedAt, List<DwordValue> Originals, bool? CompressionWasOn);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<AppliedTweak>))]
internal partial class TweakJsonContext : JsonSerializerContext;

/// <summary>
/// Persistent "ajustes finos". Unlike the boost these survive reboots, so every original value is archived
/// (admin-only file) before anything is written, and each tweak can be undone on its own.
/// Only tweaks with a measurable or documented effect; no registry-cleaner folklore.
/// </summary>
public sealed class TweakCatalog
{
    private const string Mmcss = RegistryValues.MmcssKey;

    public static readonly IReadOnlyList<TweakDefinition> All =
    [
        new("gamedvr", "Sin grabación en segundo plano",
            "Apaga la captura continua de Game Bar (GameDVR). Evita codificar vídeo mientras juegas.",
            "Medio · 1% lows", false, TweakKind.Registry,
            [new("HKCU", @"System\GameConfigStore", "GameDVR_Enabled", 0),
             new("HKCU", @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0)]),
        new("gamemode", "Modo Juego de Windows",
            "Deja que Windows priorice el juego en primer plano y aplace actualizaciones durante la partida.",
            "Bajo · estabilidad", false, TweakKind.Registry,
            [new("HKCU", @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1)]),
        new("hags", "Programación de GPU acelerada por hardware",
            "La GPU gestiona su propia cola de trabajo. Menos latencia en GPUs y drivers compatibles.",
            "Variable · latencia", true, TweakKind.Registry,
            [new("HKLM", @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2)]),
        new("mouseaccel", "Sin aceleración del ratón",
            "Desactiva 'Mejorar precisión del puntero'. Movimiento 1:1, consistente para apuntar.",
            "Alto · puntería", false, TweakKind.Registry,
            [new("HKCU", @"Control Panel\Mouse", "MouseSpeed", null), new("HKCU", @"Control Panel\Mouse", "MouseThreshold1", null),
             new("HKCU", @"Control Panel\Mouse", "MouseThreshold2", null)])
            { AfterApply = PushMouseSettings },
        new("telemetry", "Telemetría mínima",
            "Reduce el envío de datos de diagnóstico al nivel más bajo que permite tu edición de Windows.",
            "Bajo · segundo plano", false, TweakKind.Registry,
            [new("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0)]),
        new("timerres", "Resolución de temporizador global",
            "Windows 11 vuelve a respetar la resolución de temporizador pedida por el juego aunque esté en segundo plano. Avanzado.",
            "Variable · frametime", true, TweakKind.Registry,
            [new("HKLM", @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests", 1)]),
        new("memcompression", "Sin compresión de memoria",
            "Solo con 16 GB o más: evita que la CPU comprima páginas durante la partida.",
            "Bajo · CPU", true, TweakKind.MemoryCompression, []),
        new("msi", "Interrupciones MSI de la GPU (diagnóstico)",
            "Solo lectura. Indica si la tarjeta gráfica usa interrupciones por mensaje (MSI), el modo de menor latencia.",
            "Diagnóstico", false, TweakKind.Diagnostic, []),
    ];

    private static readonly Dictionary<string, uint> MouseTargets = new() { ["MouseSpeed"] = 0, ["MouseThreshold1"] = 0, ["MouseThreshold2"] = 0 };

    private readonly string _path;
    private List<AppliedTweak> _applied;

    public TweakCatalog(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TITAN", "tweaks.json");
        _applied = Load();
    }

    public int AppliedCount => _applied.Count;

    public IReadOnlyList<TweakStatus> Status() => All.Select(StatusOf).ToList();

    private TweakStatus StatusOf(TweakDefinition t)
    {
        bool applied = _applied.Any(a => a.Id == t.Id);
        switch (t.Kind)
        {
            case TweakKind.Registry:
            {
                bool active = t.Values.All(v => ReadAsText(v) == TargetText(t, v));
                return new(t, active, applied, active ? "Activo" : "Inactivo");
            }
            case TweakKind.MemoryCompression:
            {
                bool? on = CompressionEnabled();
                return new(t, on == false, applied, on switch { true => "Compresión activa", false => "Compresión apagada", _ => "Desconocido" });
            }
            default:
            {
                bool? msi = GpuUsesMsi();
                return new(t, msi == true, false, msi switch { true => "GPU en modo MSI", false => "GPU en modo línea (INTx)", _ => "No detectado" });
            }
        }
    }

    public void Apply(string id)
    {
        var t = All.First(x => x.Id == id);
        if (_applied.Any(a => a.Id == id)) return;
        if (t.Kind == TweakKind.Diagnostic) return;

        if (t.Kind == TweakKind.MemoryCompression)
        {
            bool? was = CompressionEnabled();
            Record(new AppliedTweak(id, DateTimeOffset.Now, [], was));
            RunPowerShell("Disable-MMAgent -MemoryCompression");
            return;
        }
        var originals = t.Id == "mouseaccel"   // REG_SZ values: keep the number, restore as text
            ? t.Values.Select(v => v with { Value = uint.TryParse(ReadAsText(v), out uint n) ? n : null }).ToList()
            : t.Values.Select(v => RegistryValues.ReadDword(v.Hive, v.Key, v.Name)).ToList();
        Record(new AppliedTweak(id, DateTimeOffset.Now, originals, null));         // archive first
        foreach (var v in t.Values)
            WriteTarget(t, v);
        t.AfterApply?.Invoke();
    }

    public void Revert(string id)
    {
        var a = _applied.FirstOrDefault(x => x.Id == id);
        if (a is null) return;
        var t = All.First(x => x.Id == id);
        if (t.Kind == TweakKind.MemoryCompression)
        {
            if (a.CompressionWasOn != false) RunPowerShell("Enable-MMAgent -MemoryCompression");
        }
        else
        {
            foreach (var o in a.Originals) RestoreOriginal(o);
            t.AfterApply?.Invoke();
        }
        _applied.Remove(a);
        Save();
    }

    /// <summary>System Restore point before the first persistent change (Windows rate-limits these to one per 24 h).</summary>
    public static bool TryCreateRestorePoint() =>
        RunPowerShell("Checkpoint-Computer -Description 'TITAN - ajustes finos' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop") == 0;

    // ------------------------------------------------------------------ helpers
    private static string TargetText(TweakDefinition t, DwordValue v) =>
        t.Id == "mouseaccel" ? MouseTargets[v.Name].ToString() : v.Value?.ToString() ?? "";

    private static string ReadAsText(DwordValue v)
    {
        using var k = (v.Hive == "HKLM" ? RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                                         : RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64)).OpenSubKey(v.Key);
        return k?.GetValue(v.Name)?.ToString() ?? "";
    }

    private static void WriteTarget(TweakDefinition t, DwordValue v)
    {
        if (t.Id == "mouseaccel")
        {
            // These are REG_SZ in Control Panel\Mouse
            using var k = Registry.CurrentUser.CreateSubKey(v.Key, true);
            k.SetValue(v.Name, MouseTargets[v.Name].ToString(), RegistryValueKind.String);
            return;
        }
        RegistryValues.Write(v);
    }

    private static void RestoreOriginal(DwordValue original)
    {
        if (original.Key == @"Control Panel\Mouse")
        {
            using var k = Registry.CurrentUser.CreateSubKey(original.Key, true);
            k.SetValue(original.Name, (original.Value ?? (original.Name == "MouseSpeed" ? 1u : original.Name == "MouseThreshold1" ? 6u : 10u)).ToString(), RegistryValueKind.String);
            return;
        }
        RegistryValues.Write(original);
    }

    private void Record(AppliedTweak a) { _applied.Add(a); Save(); }

    private List<AppliedTweak> Load()
    {
        try { return File.Exists(_path) ? JsonSerializer.Deserialize(File.ReadAllText(_path), TweakJsonContext.Default.ListAppliedTweak) ?? [] : []; }
        catch { return []; }
    }

    private void Save()
    {
        Journal.EnsureSecureDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_applied, TweakJsonContext.Default.ListAppliedTweak));
    }

    private static bool? CompressionEnabled()
    {
        var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command \"(Get-MMAgent).MemoryCompression\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        try
        {
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(8000);
            return o.Equals("True", StringComparison.OrdinalIgnoreCase) ? true : o.Equals("False", StringComparison.OrdinalIgnoreCase) ? false : null;
        }
        catch { return null; }
    }

    private static int RunPowerShell(string command)
    {
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -Command \"{command}\"") { UseShellExecute = false, CreateNoWindow = true };
        try
        {
            using var p = Process.Start(psi)!;
            p.WaitForExit(60_000);
            return p.HasExited ? p.ExitCode : -1;
        }
        catch { return -1; }
    }

    private static bool? GpuUsesMsi()
    {
        using var pci = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\PCI");
        if (pci is null) return null;
        foreach (var dev in pci.GetSubKeyNames())
        {
            using var d = pci.OpenSubKey(dev);
            if (d is null) continue;
            foreach (var inst in d.GetSubKeyNames())
            {
                using var i = d.OpenSubKey(inst);
                if (i?.GetValue("ClassGUID") as string != "{4d36e968-e325-11ce-bfc1-08002be10318}") continue;   // Display adapters
                using var msi = i.OpenSubKey(@"Device Parameters\Interrupt Management\MessageSignaledInterruptProperties");
                return msi?.GetValue("MSISupported") is int v && v == 1;
            }
        }
        return null;
    }

    // SystemParametersInfo(SPI_SETMOUSE) so the mouse change applies without logging off.
    private static void PushMouseSettings()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Mouse");
        int Read(string n, int d) => int.TryParse(k?.GetValue(n) as string, out int v) ? v : d;
        int[] values = [Read("MouseThreshold1", 6), Read("MouseThreshold2", 10), Read("MouseSpeed", 1)];
        SystemParametersInfo(0x0004 /* SPI_SETMOUSE */, 0, values, 0x01 | 0x02);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, int[] values, uint winIni);
}
