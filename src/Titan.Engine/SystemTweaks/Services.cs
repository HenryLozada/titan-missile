using System.ServiceProcess;

namespace Titan.Engine.SystemTweaks;

/// <summary>
/// Pauses background services for the session. Only stops/starts — never touches the start type,
/// so a crash can never leave a service permanently disabled.
/// </summary>
public static class Services
{
    public static readonly IReadOnlyList<string> DefaultPause = ["SysMain", "WSearch", "DiagTrack", "Spooler", "MapsBroker"];

    /// <summary>
    /// LÍMITE adds what downloads, scans and reports in the background mid-game: Windows Update, BITS,
    /// Delivery Optimization, error reporting and the compatibility assistant. All restart on demand anyway.
    /// </summary>
    public static readonly IReadOnlyList<string> MaxPause = [.. DefaultPause, "wuauserv", "BITS", "DoSvc", "WerSvc", "PcaSvc"];

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public static bool IsRunning(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch (InvalidOperationException) { return false; } // not installed
    }

    /// <summary>Running and stoppable: services Windows won't let us stop are skipped instead of reported as errors.</summary>
    public static bool CanPause(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            return sc.Status == ServiceControllerStatus.Running && sc.CanStop;
        }
        catch (InvalidOperationException) { return false; }
    }

    public static void Stop(string name)
    {
        using var sc = new ServiceController(name);
        if (sc.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending) return;
        if (!sc.CanStop) throw new InvalidOperationException($"{name} no se puede detener.");
        sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, Timeout);
    }

    public static void Start(string name)
    {
        using var sc = new ServiceController(name);
        if (sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending) return;
        sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, Timeout);
    }
}
