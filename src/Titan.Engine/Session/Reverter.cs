using Titan.Engine.Processes;
using Titan.Engine.SystemTweaks;

namespace Titan.Engine.Session;

public interface IChangeReverter
{
    void Revert(JournalEntry entry);
}

/// <summary>Undoes journal entries against the real system. Every undo is safe to run twice.</summary>
public sealed class SystemReverter : IChangeReverter
{
    /// <summary>The only registry values a journal may ask us to restore — a tampered journal can't write anything else.</summary>
    private static readonly HashSet<(string Hive, string Key, string Name)> AllowedRegistry =
    [
        ("HKLM", RegistryValues.MmcssKey, "SystemResponsiveness"),
        ("HKLM", RegistryValues.MmcssKey, "NetworkThrottlingIndex"),
    ];

    public void Revert(JournalEntry entry)
    {
        switch (entry)
        {
            case PowerPlanChanged e:
                if (!PowerPlans.TrySetActive(e.Previous)) throw new InvalidOperationException("No se pudo restaurar el plan de energía.");
                break;
            case PowerOverlayChanged e:
                PowerPlans.TrySetOverlay(e.Previous);
                break;
            case ServiceStopped e:
                Services.Start(e.Name);
                break;
            case ProcessFrozen e:
                if (ProcessOps.IsAlive(new(e.Pid, e.CreationTime))) ProcessOps.Resume(e.Pid);
                break;
            case PriorityChanged e:
                if (ProcessOps.IsAlive(new(e.Pid, e.CreationTime))) ProcessOps.SetPriority(e.Pid, e.Previous);
                break;
            case EcoQoSApplied e:
                if (ProcessOps.IsAlive(new(e.Pid, e.CreationTime))) ProcessOps.SetEcoQoS(e.Pid, false);
                break;
            case CpuSetsChanged e:
                if (ProcessOps.IsAlive(new(e.Pid, e.CreationTime))) ProcessOps.SetDefaultCpuSets(e.Pid, e.Previous);
                break;
            case RegistryChanged e:
                if (!AllowedRegistry.Contains((e.Previous.Hive, e.Previous.Key, e.Previous.Name)))
                    throw new InvalidOperationException($"Valor de registro no permitido en el journal: {e.Previous.Key}\\{e.Previous.Name}");
                RegistryValues.Write(e.Previous);
                break;
            default:
                throw new NotSupportedException(entry.GetType().Name);
        }
    }
}
