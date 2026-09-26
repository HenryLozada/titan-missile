using System.Diagnostics;
using Titan.Engine.Memory;
using Titan.Engine.Processes;
using Titan.Engine.SystemTweaks;

namespace Titan.Engine.Session;

public sealed record BoostOptions
{
    public int? GamePid { get; init; }
    public bool PowerPlan { get; init; } = true;
    public bool PauseServices { get; init; } = true;
    public bool GamePriority { get; init; } = true;
    public bool GameCpuSets { get; init; } = true;
    public bool FreezeBackground { get; init; } = true;
    public bool ThrottleBackground { get; init; } = true;
    /// <summary>MMCSS SystemResponsiveness / NetworkThrottlingIndex. Low impact; kept because it's cheap and reversible.</summary>
    public bool MmcssTweaks { get; init; } = true;
    public PurgeOps Purge { get; init; } = PurgeOps.Boost;
    public IReadOnlyList<string> ServicesToPause { get; init; } = Services.DefaultPause;
    public ProcessPolicy Policy { get; init; } = new();
}

/// <summary>Progress for the UI ring: it fills with real work, not a fake timer.</summary>
public sealed record BoostProgress(int Step, int Total, string Label);

public sealed record BoostReport(
    TimeSpan Elapsed,
    string? GameName,
    Guid? PowerPlan,
    IReadOnlyList<string> ServicesPaused,
    IReadOnlyList<string> Frozen,
    IReadOnlyList<string> Throttled,
    GameCorePlan? CorePlan,
    PurgeResult? Purge,
    IReadOnlyList<string> Warnings);

public sealed record RestoreReport(int Reverted, IReadOnlyList<string> Failures, TimeSpan Elapsed);

public sealed class BoostEngine(Journal journal, IChangeReverter reverter)
{
    public BoostEngine() : this(new Journal(), new SystemReverter()) { }

    public Journal Journal => journal;

    public BoostReport Engage(BoostOptions o, IProgress<BoostProgress>? progress = null)
    {
        var sw = Stopwatch.StartNew();
        var warnings = new List<string>();
        var procs = ProcessOps.Snapshot();
        string? gameName = o.GamePid is int gp ? procs.FirstOrDefault(p => p.Pid == gp)?.Name : null;
        if (o.GamePid is not null && gameName is null) warnings.Add($"El PID {o.GamePid} no existe; se hace boost sin juego objetivo.");

        journal.Begin(gameName is null ? null : o.GamePid, gameName);

        const int total = 6;
        int step = 0;
        void Report(string label) => progress?.Report(new BoostProgress(++step, total, label));

        // Apply one change: log intent first, then apply; forget the intent if apply failed.
        bool Apply(JournalEntry intent, Action apply, string what)
        {
            journal.Record(intent);
            try { apply(); return true; }
            catch (Exception ex) { journal.Forget(intent); warnings.Add($"{what}: {ex.Message}"); return false; }
        }

        // 1. Power
        Guid? plan = null;
        if (o.PowerPlan)
        {
            var previous = PowerPlans.GetActive();
            Guid target = PowerPlans.EnsureTitanUltimate() ? PowerPlans.TitanUltimate
                        : PowerPlans.Exists(PowerPlans.HighPerformance) ? PowerPlans.HighPerformance : previous;
            if (target != previous && Apply(new PowerPlanChanged(previous), () =>
                { if (!PowerPlans.TrySetActive(target)) throw new InvalidOperationException("PowerSetActiveScheme rechazado."); }, "Plan de energía"))
                plan = target;

            if (PowerPlans.TryGetOverlay() is Guid overlay && overlay != PowerPlans.OverlayBestPerformance)
                Apply(new PowerOverlayChanged(overlay), () => PowerPlans.TrySetOverlay(PowerPlans.OverlayBestPerformance), "Modo de energía");
        }
        Report("Energía al máximo");

        // 2. Services
        var paused = new List<string>();
        if (o.PauseServices)
        {
            foreach (var name in o.ServicesToPause.Where(Services.IsRunning))
                if (Apply(new ServiceStopped(name), () => Services.Stop(name), $"Servicio {name}"))
                    paused.Add(name);
        }
        Report("Servicios en pausa");

        // 3. The game
        GameCorePlan? corePlan = null;
        if (o.GamePid is int pid && gameName is not null && ProcessOps.TryGetKey(pid, out var gameKey))
        {
            if (o.GamePriority)
            {
                var prev = ProcessOps.GetPriority(pid);
                if (prev is not PriorityClass.High and not PriorityClass.Realtime)
                    Apply(new PriorityChanged(pid, gameKey.CreationTime, gameName, prev),
                          () => ProcessOps.SetPriority(pid, PriorityClass.High), "Prioridad del juego");
            }
            if (o.GameCpuSets)
            {
                corePlan = CpuTopology.Read().PlanForGame();
                if (corePlan.CpuSetIds.Length > 0)
                {
                    var prev = ProcessOps.GetDefaultCpuSets(pid);
                    Apply(new CpuSetsChanged(pid, gameKey.CreationTime, gameName, prev),
                          () => ProcessOps.SetDefaultCpuSets(pid, corePlan.CpuSetIds), "CPU sets del juego");
                }
            }
        }
        Report("Juego priorizado");

        // 4. Background apps
        var frozen = new List<string>();
        var throttled = new List<string>();
        if (o.FreezeBackground || o.ThrottleBackground)
        {
            var tree = ProcessPolicy.GameTree(procs, o.GamePid);
            int self = Environment.ProcessId;
            foreach (var p in procs)
            {
                var action = o.Policy.Classify(p, self, tree);
                if (!ProcessOps.TryGetKey(p.Pid, out var key)) continue;

                if (action == ProcessAction.Freeze && o.FreezeBackground)
                {
                    if (Apply(new ProcessFrozen(p.Pid, key.CreationTime, p.Name), () => ProcessOps.Suspend(p.Pid), $"Congelar {p.Name}"))
                        frozen.Add(p.Name);
                }
                else if (action == ProcessAction.Throttle && o.ThrottleBackground)
                {
                    bool any = false;
                    try
                    {
                        var prev = ProcessOps.GetPriority(p.Pid);
                        if (prev is PriorityClass.Normal or PriorityClass.AboveNormal)
                            any |= Apply(new PriorityChanged(p.Pid, key.CreationTime, p.Name, prev),
                                         () => ProcessOps.SetPriority(p.Pid, PriorityClass.BelowNormal), $"Prioridad {p.Name}");
                    }
                    catch (Exception ex) { warnings.Add($"Prioridad {p.Name}: {ex.Message}"); }
                    any |= Apply(new EcoQoSApplied(p.Pid, key.CreationTime, p.Name), () => ProcessOps.SetEcoQoS(p.Pid, true), $"EcoQoS {p.Name}");
                    if (any) throttled.Add(p.Name);
                }
            }
        }
        Report("Segundo plano bajo control");

        // 5. MMCSS
        if (o.MmcssTweaks)
        {
            SetDword("HKLM", RegistryValues.MmcssKey, "SystemResponsiveness", 10);
            SetDword("HKLM", RegistryValues.MmcssKey, "NetworkThrottlingIndex", 0xFFFFFFFF);
        }
        Report("Planificador multimedia");

        // 6. Memory — last, so frozen/throttled apps' freed pages are purged too
        PurgeResult? purge = null;
        if (o.Purge != PurgeOps.None)
        {
            purge = MemoryPurger.Execute(o.Purge);
            warnings.AddRange(purge.Steps.Where(s => !s.Ok).Select(s => $"Memoria {s.Op}: {s.Error}"));
        }
        Report("Memoria liberada");

        return new BoostReport(sw.Elapsed, gameName, plan, paused,
            Collapse(frozen), Collapse(throttled), corePlan, purge, warnings);

        void SetDword(string hive, string key, string name, uint value)
        {
            var prev = RegistryValues.ReadDword(hive, key, name);
            if (prev.Value == value) return;
            Apply(new RegistryChanged(prev), () => RegistryValues.Write(prev with { Value = value }), name);
        }
    }

    /// <summary>Undoes the open session (this run, or one left behind by a crash). Newest change first.</summary>
    public RestoreReport Restore()
    {
        var sw = Stopwatch.StartNew();
        var doc = journal.Current ?? journal.Load();
        if (doc is null) return new RestoreReport(0, [], sw.Elapsed);

        var failures = new List<string>();
        int ok = 0;
        for (int i = doc.Entries.Count - 1; i >= 0; i--)
        {
            var entry = doc.Entries[i];
            try { reverter.Revert(entry); ok++; }
            catch (Exception ex) { failures.Add($"{entry.Describe()}: {ex.Message}"); }
        }
        journal.Close();
        return new RestoreReport(ok, failures, sw.Elapsed);
    }

    /// <summary>"chrome.exe ×12" instead of twelve lines.</summary>
    private static List<string> Collapse(List<string> names) =>
        names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
             .Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key)
             .ToList();
}
