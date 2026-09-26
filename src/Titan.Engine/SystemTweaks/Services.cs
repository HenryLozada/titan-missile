using System.ServiceProcess;

namespace Titan.Engine.SystemTweaks;

/// <summary>
/// Pauses background services for the session. Only stops/starts — never touches the start type,
/// so a crash can never leave a service permanently disabled.
/// </summary>
public static class Services
{
    public static readonly IReadOnlyList<string> DefaultPause = ["SysMain", "WSearch", "DiagTrack", "Spooler", "MapsBroker"];

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
