using System.Diagnostics;
using Titan.Engine.Memory;
using Titan.Engine.Native;
using Titan.Engine.Processes;
using Titan.Engine.SystemTweaks;

namespace Titan.Engine.Session;

public sealed record BoostOptions
{
    public int? GamePid { get; init; }
    /// <summary>Install folder: any process started from it (the real exe behind a bootstrapper) is the game too.</summary>
    public string? GameDir { get; init; }
    public BoostLevel Level { get; init; } = BoostLevel.Max;
    public bool PowerPlan { get; init; } = true;
    public bool PauseServices { get; init; } = true;
    public bool GamePriority { get; init; } = true;
    public bool GameCpuSets { get; init; } = true;
    public bool GameGpuPriority { get; init; } = true;
    public bool FreezeBackground { get; init; } = true;
    public bool ThrottleBackground { get; init; } = true;
    /// <summary>MMCSS SystemResponsiveness / NetworkThrottlingIndex. Low impact; kept because it's cheap and reversible.</summary>
    public bool MmcssTweaks { get; init; } = true;
    /// <summary>Only applied where it actually reaches the game (see <see cref="SystemTweaks.TimerResolution"/>).</summary>
    public bool TimerResolution { get; init; } = true;
    public PurgeOps Purge { get; init; } = PurgeOps.Boost;
    /// <summary>Null = by level (<see cref="Services.DefaultPause"/> or <see cref="Services.MaxPause"/>).</summary>
    public IReadOnlyList<string>? ServicesToPause { get; init; }
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
    IReadOnlyList<string> Warnings)
{
    public BoostLevel Level { get; init; }
    /// <summary>What the game got: "CPU alta", "GPU alta", "núcleos P", …</summary>
    public IReadOnlyList<string> GameBoosts { get; init; } = [];
    public int PowerSettings { get; init; }
    public double? TimerMs { get; init; }
    /// <summary>Background processes whose memory was handed back before the purge.</summary>
    public int Trimmed { get; init; }
}

public sealed record RestoreReport(int Reverted, IReadOnlyList<string> Failures, TimeSpan Elapsed);

/// <summary>What the governor has done since the boost. Pushed to the UI after every tick that did something.</summary>
public sealed record GovernorStats(
    int SurgicalPurges, int FullPurges, long FreedBytes, int Tamed, int GameProcesses, string? LastAction, DateTimeOffset? LastActionAt)
{
    public int Purges => SurgicalPurges + FullPurges;
}

public sealed class BoostEngine(Journal journal, IChangeReverter reverter)
{
    public const int Steps = 7;

    private readonly object _gate = new();
    private Session? _session;
    private CancellationTokenSource? _governor;

    public BoostEngine() : this(new Journal(), new SystemReverter()) { }

    public Journal Journal => journal;
    public GovernorStats? Stats => _session?.Stats;

    /// <summary>Live bookkeeping of the open session (this process only; the journal is what survives a crash).</summary>
    private sealed class Session(BoostOptions options)
    {
        public BoostOptions Options { get; } = options;
        public HashSet<int> Seen { get; } = [];
        public HashSet<int> Protected { get; } = [];
        public Dictionary<int, ProcessKey> Game { get; } = [];
        /// <summary>Tamed processes whose working set may be trimmed (frozen always; throttled in LÍMITE unless voice/music).</summary>
        public Dictionary<int, ProcessKey> Trimmable { get; } = [];
        /// <summary>Game processes that had no GPU device yet when we tried: retried every tick until they render.</summary>
        public Dictionary<int, (ProcessKey Key, string Name)> GpuPending { get; } = [];
        public GameCorePlan? CorePlan { get; set; }
        public int Unreachable { get; set; }
        public DateTimeOffset LastSurgical { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset LastFull { get; set; } = DateTimeOffset.MinValue;
        public GovernorStats Stats { get; set; } = new(0, 0, 0, 0, 0, null, null);
    }

    // ================================================================== engage

    public BoostReport Engage(BoostOptions o, IProgress<BoostProgress>? progress = null)
    {
        var sw = Stopwatch.StartNew();
        var warnings = new List<string>();
        var procs = ProcessOps.Snapshot();
        string? gameName = o.GamePid is int gp ? procs.FirstOrDefault(p => p.Pid == gp)?.Name : null;
        if (o.GamePid is not null && gameName is null) warnings.Add($"El PID {o.GamePid} no existe; se hace boost sin juego objetivo.");
        bool max = o.Level == BoostLevel.Max;

        lock (_gate)
        {
            journal.Begin(gameName is null ? null : o.GamePid, gameName);
            var s = _session = new Session(o);

            int step = 0;
            void Report(string label) => progress?.Report(new BoostProgress(++step, Steps, label));

            // 1. Power
            Guid? plan = null;
            int powerSettings = 0;
            if (o.PowerPlan)
            {
                var previous = PowerPlans.GetActive();
                bool titan = PowerPlans.EnsureTitanUltimate();
                if (titan) powerSettings = SafeCount(PowerPlans.TuneTitanUltimate, "Ajustes del plan", warnings);
                Guid target = titan ? PowerPlans.TitanUltimate
                            : PowerPlans.Exists(PowerPlans.HighPerformance) ? PowerPlans.HighPerformance : previous;
                if (target != previous && Apply(new PowerPlanChanged(previous), () =>
                    { if (!PowerPlans.TrySetActive(target)) throw new InvalidOperationException("PowerSetActiveScheme rechazado."); }, "Plan de energía", warnings))
                    plan = target;
                else if (target == previous) plan = target;

                if (PowerPlans.TryGetOverlay() is Guid overlay && overlay != PowerPlans.OverlayBestPerformance)
                    Apply(new PowerOverlayChanged(overlay), () => PowerPlans.TrySetOverlay(PowerPlans.OverlayBestPerformance), "Modo de energía", warnings);
            }
            Report("Energía al máximo");

            // 2. Services
            var paused = new List<string>();
            if (o.PauseServices)
            {
                foreach (var name in (o.ServicesToPause ?? (max ? Services.MaxPause : Services.DefaultPause)).Where(Services.CanPause))
                    if (Apply(new ServiceStopped(name), () => Services.Stop(name), $"Servicio {name}", warnings))
                        paused.Add(name);
            }
            Report("Servicios en pausa");

            // 3. The game: its own process, everything it spawned, and anything running from its folder
            var gameBoosts = new SortedSet<string>();
            foreach (var t in ProcessPolicy.GameTree(procs, o.GamePid)) s.Protected.Add(t);
            foreach (var p in GameMembers(procs, s, fresh: procs))
                foreach (var label in Empower(s, p, warnings)) gameBoosts.Add(label);
            Report("Juego priorizado");

            // 4. Background apps
            var frozen = new List<string>();
            var throttled = new List<string>();
            foreach (var p in procs)
            {
                if (!s.Seen.Add(p.Pid)) continue;
                switch (Tame(s, p))
                {
                    case ProcessAction.Freeze: frozen.Add(p.Name); break;
                    case ProcessAction.Throttle: throttled.Add(p.Name); break;
                }
            }
            Report("Segundo plano bajo control");

            // 5. Scheduler: MMCSS + timer
            if (o.MmcssTweaks)
            {
                SetDword("HKLM", RegistryValues.MmcssKey, "SystemResponsiveness", 10);
                SetDword("HKLM", RegistryValues.MmcssKey, "NetworkThrottlingIndex", 0xFFFFFFFF);
            }
            double? timer = o.TimerResolution ? SystemTweaks.TimerResolution.Raise() : null;
            Report("Planificador y temporizador");

            // 6. Surgical: hand the tamed apps' pages back and purge only low-priority standby
            var before = MemoryLists.Read();
            int trimmed = TrimTamed(s);
            var steps = new List<PurgeStep>();
            if (o.Purge != PurgeOps.None)
                steps.AddRange(MemoryPurger.Execute(PurgeOps.StandbyLowPriority).Steps);
            Report("Memoria de fondo recortada");

            // 7. The rest of the cache — last, so everything freed above is swept too
            PurgeResult? purge = null;
            if (o.Purge != PurgeOps.None)
            {
                var ops = (o.Purge & ~PurgeOps.StandbyLowPriority) | (max ? PurgeOps.ModifiedList : PurgeOps.None);
                var r = MemoryPurger.Execute(ops);
                steps.AddRange(r.Steps);
                purge = new PurgeResult(before, r.After, steps, sw.Elapsed);
                warnings.AddRange(steps.Where(x => !x.Ok).Select(x => $"Memoria {x.Op}: {x.Error}"));
            }
            s.LastFull = s.LastSurgical = DateTimeOffset.UtcNow;
            Report("Caché liberada");

            if (s.Unreachable > 0) warnings.Add($"{s.Unreachable} procesos protegidos por Windows se dejaron como estaban.");
            s.Stats = s.Stats with { GameProcesses = s.Game.Count };

            return new BoostReport(sw.Elapsed, gameName, plan, paused,
                Collapse(frozen), Collapse(throttled), s.CorePlan, purge, warnings)
            {
                Level = o.Level, GameBoosts = [.. gameBoosts], PowerSettings = powerSettings, TimerMs = timer, Trimmed = trimmed,
            };
        }

        void SetDword(string hive, string key, string name, uint value)
        {
            var prev = RegistryValues.ReadDword(hive, key, name);
            if (prev.Value == value) return;
            Apply(new RegistryChanged(prev), () => RegistryValues.Write(prev with { Value = value }), name, warnings);
        }
    }

    // ================================================================== governor: keeps the session sharp while you play

    /// <summary>
    /// Every 2 s: new processes from the game folder get the game treatment, new background apps get tamed,
    /// and memory is reclaimed in tiers when the free list runs low. Stops on <see cref="Restore"/>.
    /// </summary>
    public void StartGovernor(Action<GovernorStats>? changed = null, TimeSpan? interval = null)
    {
        lock (_gate)
        {
            if (_session is null || _governor is not null) return;
            _governor = new CancellationTokenSource();
            var ct = _governor.Token;
            _ = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(interval ?? TimeSpan.FromSeconds(2));
                try
                {
                    while (await timer.WaitForNextTickAsync(ct))
                    {
                        GovernorStats? after = null;
                        try { after = Tick(ct); } catch { /* one bad tick must not end the session */ }
                        if (after is not null) changed?.Invoke(after);
                    }
                }
                catch (OperationCanceledException) { }
            }, ct);
        }
    }

    /// <summary>One governor pass. Returns the stats if something happened.</summary>
    public GovernorStats? Tick(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_session is not { } s || ct.IsCancellationRequested) return null;
            var before = s.Stats;
            var procs = ProcessOps.Snapshot();

            var alive = procs.Select(p => p.Pid).ToHashSet();
            s.Seen.RemoveWhere(pid => !alive.Contains(pid));
            foreach (var dead in s.Game.Keys.Where(k => !alive.Contains(k)).ToList()) s.Game.Remove(dead);
            foreach (var dead in s.Trimmable.Keys.Where(k => !alive.Contains(k)).ToList()) s.Trimmable.Remove(dead);
            foreach (var dead in s.GpuPending.Keys.Where(k => !alive.Contains(k)).ToList()) s.GpuPending.Remove(dead);

            foreach (var (pid, (key, name)) in s.GpuPending.ToList())
                if (TryGpu(s, pid, key, name, null) is true)
                    s.Stats = s.Stats with { LastAction = $"{name}: GPU en prioridad alta", LastActionAt = DateTimeOffset.Now };

            var fresh = procs.Where(p => !s.Seen.Contains(p.Pid)).ToList();
            if (fresh.Count > 0)
            {
                var ignored = new List<string>();
                int newGame = 0, tamed = 0;
                foreach (var p in GameMembers(procs, s, fresh)) { Empower(s, p, ignored); newGame++; }
                foreach (var p in fresh)
                {
                    if (!s.Seen.Add(p.Pid)) continue;
                    if (Tame(s, p) is ProcessAction.Freeze or ProcessAction.Throttle) tamed++;
                }
                if (newGame > 0 || tamed > 0)
                    s.Stats = s.Stats with
                    {
                        Tamed = s.Stats.Tamed + tamed, GameProcesses = s.Game.Count,
                        LastAction = newGame > 0 ? $"{newGame} proceso(s) nuevo(s) del juego priorizado(s)" : $"{tamed} proceso(s) nuevo(s) moderado(s)",
                        LastActionAt = DateTimeOffset.Now,
                    };
            }

            // Memory, in tiers
            var mem = MemoryLists.Read();
            var now = DateTimeOffset.UtcNow;
            var action = MemoryPressure.Decide(mem, PressureOptions.For(mem.TotalBytes, s.Options.Level), now, s.LastSurgical, s.LastFull);
            if (action != PressureAction.None)
            {
                int trimmed = TrimTamed(s);
                var r = MemoryPurger.Execute(action == PressureAction.Full ? PurgeOps.Boost : PurgeOps.StandbyLowPriority);
                long freed = Math.Max(0, Math.Max((long)r.After.FreeBytes - (long)mem.FreeBytes, (long)mem.InUseBytes - (long)r.After.InUseBytes));
                s.LastSurgical = now;
                if (action == PressureAction.Full) s.LastFull = now;
                s.Stats = s.Stats with
                {
                    SurgicalPurges = s.Stats.SurgicalPurges + (action == PressureAction.Surgical ? 1 : 0),
                    FullPurges = s.Stats.FullPurges + (action == PressureAction.Full ? 1 : 0),
                    FreedBytes = s.Stats.FreedBytes + freed,
                    LastAction = action == PressureAction.Full
                        ? $"Purga completa: {freed / 1048576.0:0} MB devueltos al juego"
                        : $"Purga quirúrgica: {trimmed} apps de fondo recortadas, {freed / 1048576.0:0} MB devueltos al juego",
                    LastActionAt = DateTimeOffset.Now,
                };
            }
            return s.Stats == before ? null : s.Stats;
        }
    }

    private void StopGovernor()
    {
        _governor?.Cancel();
        _governor = null;
    }

    // ================================================================== restore

    /// <summary>Undoes the open session (this run, or one left behind by a crash). Newest change first.</summary>
    public RestoreReport Restore()
    {
        var sw = Stopwatch.StartNew();
        lock (_gate)
        {
            StopGovernor();
            _session = null;
            SystemTweaks.TimerResolution.Release();

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
    }

    // ================================================================== building blocks

    /// <summary>Log intent first, then apply; forget the intent if apply failed. Null warnings = fail quietly.</summary>
    private bool Apply(JournalEntry intent, Action apply, string what, List<string>? warnings)
    {
        journal.Record(intent);
        try { apply(); return true; }
        catch (Exception ex) { journal.Forget(intent); warnings?.Add($"{what}: {ex.Message}"); return false; }
    }

    /// <summary>The game root, its descendants, and anything launched from its install folder (minus crash reporters).</summary>
    private static List<ProcessEntry> GameMembers(IReadOnlyList<ProcessEntry> all, Session s, IReadOnlyList<ProcessEntry> fresh)
    {
        var members = new List<ProcessEntry>();
        var known = new HashSet<int>(s.Game.Keys);
        if (s.Options.GamePid is int root && all.FirstOrDefault(p => p.Pid == root) is { } rp && !known.Contains(root) && fresh.Contains(rp))
        {
            members.Add(rp); known.Add(root);
        }
        bool grew = true;
        while (grew)                       // descendants, in whatever order the snapshot lists them
        {
            grew = false;
            foreach (var p in fresh)
                if (!known.Contains(p.Pid) && known.Contains(p.ParentPid) && p.SessionId != 0 && !ProcessPolicy.IsGameHelper(p.Name))
                {
                    members.Add(p); known.Add(p.Pid); grew = true;
                }
        }
        if (s.Options.GameDir is { Length: > 3 } dir)
            foreach (var p in fresh)
                if (!known.Contains(p.Pid) && p.SessionId != 0 && !ProcessPolicy.IsGameHelper(p.Name)
                    && ProcessPolicy.IsUnder(ProcessOps.ImagePath(p.Pid), dir))
                {
                    members.Add(p); known.Add(p.Pid);
                }
        return members;
    }

    /// <summary>CPU priority, CPU sets, GPU priority and no power throttling — each journaled, each optional.</summary>
    private IEnumerable<string> Empower(Session s, ProcessEntry p, List<string> warnings)
    {
        var o = s.Options;
        s.Seen.Add(p.Pid);
        s.Protected.Add(p.Pid);
        if (!ProcessOps.TryGetKey(p.Pid, out var key)) { warnings.Add($"No pude abrir {p.Name}: puede estar protegido por su anti-cheat."); return []; }
        s.Game[p.Pid] = key;
        var labels = new List<string>();

        if (o.GamePriority)
        {
            try
            {
                var prev = ProcessOps.GetPriority(p.Pid);
                if (prev is PriorityClass.High or PriorityClass.Realtime) labels.Add("CPU alta");
                else if (Apply(new PriorityChanged(p.Pid, key.CreationTime, p.Name, prev),
                               () => ProcessOps.SetPriority(p.Pid, PriorityClass.High), $"Prioridad de {p.Name}", warnings))
                    labels.Add("CPU alta");
            }
            catch (Exception ex) { warnings.Add($"Prioridad de {p.Name}: {ex.Message}"); }
        }
        if (o.GameCpuSets)
        {
            s.CorePlan ??= CpuTopology.Read().PlanForGame();
            if (s.CorePlan.CpuSetIds.Length > 0)
            {
                try
                {
                    var prev = ProcessOps.GetDefaultCpuSets(p.Pid);
                    if (Apply(new CpuSetsChanged(p.Pid, key.CreationTime, p.Name, prev),
                              () => ProcessOps.SetDefaultCpuSets(p.Pid, s.CorePlan.CpuSetIds), $"Núcleos de {p.Name}", warnings))
                        labels.Add("núcleos rápidos");
                }
                catch (Exception ex) { warnings.Add($"Núcleos de {p.Name}: {ex.Message}"); }
            }
        }
        if (Apply(new HighQoSApplied(p.Pid, key.CreationTime, p.Name), () => ProcessOps.SetHighQoS(p.Pid), $"QoS de {p.Name}", warnings))
            labels.Add("sin estrangulamiento");
        if (o.GameGpuPriority && TryGpu(s, p.Pid, key, p.Name, warnings) is not false)
            labels.Add("GPU alta");
        return labels;
    }

    /// <summary>
    /// True = GPU priority is high; null = no GPU device yet (queued for the governor); false = refused.
    /// </summary>
    private bool? TryGpu(Session s, int pid, ProcessKey key, string name, List<string>? warnings)
    {
        try
        {
            int prev = ProcessOps.GetGpuPriority(pid);
            bool ok = prev >= 4 || Apply(new GpuPriorityChanged(pid, key.CreationTime, name, prev),
                                         () => ProcessOps.SetGpuPriority(pid, 4), $"GPU de {name}", warnings);
            s.GpuPending.Remove(pid);
            return ok;
        }
        catch (NtStatusException ex) when (ex.Status == ProcessOps.STATUS_INVALID_PARAMETER)
        {
            s.GpuPending[pid] = (key, name);
            return null;
        }
        catch (Exception ex)
        {
            s.GpuPending.Remove(pid);
            warnings?.Add($"GPU de {name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Freeze or throttle one background process per policy. Failures on protected processes are counted, not listed.</summary>
    private ProcessAction Tame(Session s, ProcessEntry p)
    {
        var o = s.Options;
        bool max = o.Level == BoostLevel.Max;
        var action = o.Policy.Classify(p, Environment.ProcessId, s.Protected);
        if (action is not (ProcessAction.Freeze or ProcessAction.Throttle)) return ProcessAction.None;
        if (!ProcessOps.TryGetKey(p.Pid, out var key)) { s.Unreachable++; return ProcessAction.None; }
        bool responsive = ProcessPolicy.Responsive.Contains(p.Name);

        if (action == ProcessAction.Freeze)
        {
            if (!o.FreezeBackground || !Apply(new ProcessFrozen(p.Pid, key.CreationTime, p.Name), () => ProcessOps.Suspend(p.Pid), p.Name, null))
            {
                s.Unreachable += o.FreezeBackground ? 1 : 0;
                return ProcessAction.None;
            }
            if (!responsive && Demote(s, p, key)) s.Trimmable[p.Pid] = key;
            return ProcessAction.Freeze;
        }

        if (!o.ThrottleBackground) return ProcessAction.None;
        bool any = false;
        try
        {
            var prev = ProcessOps.GetPriority(p.Pid);
            if (prev is PriorityClass.Normal or PriorityClass.AboveNormal)
                any |= Apply(new PriorityChanged(p.Pid, key.CreationTime, p.Name, prev),
                             () => ProcessOps.SetPriority(p.Pid, PriorityClass.BelowNormal), p.Name, null);
        }
        catch { }
        any |= Apply(new EcoQoSApplied(p.Pid, key.CreationTime, p.Name), () => ProcessOps.SetEcoQoS(p.Pid, true), p.Name, null);
        if (max)
        {
            try
            {
                int prevIo = ProcessOps.GetIoPriority(p.Pid);
                if (prevIo > 1)
                    any |= Apply(new IoPriorityChanged(p.Pid, key.CreationTime, p.Name, prevIo), () => ProcessOps.SetIoPriority(p.Pid, 1), p.Name, null);
            }
            catch { }
        }
        if (!any) { s.Unreachable++; return ProcessAction.None; }
        if (!responsive && Demote(s, p, key) && max) s.Trimmable[p.Pid] = key;
        return ProcessAction.Throttle;
    }

    /// <summary>Very-low page priority: once trimmed, its pages are what Windows hands to the game first — never the game's own cache.</summary>
    private bool Demote(Session s, ProcessEntry p, ProcessKey key)
    {
        try
        {
            uint prev = ProcessOps.GetMemoryPriority(p.Pid);
            if (prev <= 1) return true;
            return Apply(new MemoryPriorityChanged(p.Pid, key.CreationTime, p.Name, prev),
                         () => ProcessOps.SetMemoryPriority(p.Pid, 1), p.Name, null);
        }
        catch { return false; }
    }

    /// <summary>Empties the working sets of the trimmable tamed apps (never the game, never voice/music).</summary>
    private static int TrimTamed(Session s)
    {
        int n = 0;
        foreach (var (pid, key) in s.Trimmable)
            if (ProcessOps.IsAlive(key) && ProcessOps.TrimWorkingSet(pid)) n++;
        return n;
    }

    private static int SafeCount(Func<int> f, string what, List<string> warnings)
    {
        try { return f(); }
        catch (Exception ex) { warnings.Add($"{what}: {ex.Message}"); return 0; }
    }

    /// <summary>"chrome.exe ×12" instead of twelve lines.</summary>
    private static List<string> Collapse(List<string> names) =>
        names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
             .Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key)
             .ToList();
}
