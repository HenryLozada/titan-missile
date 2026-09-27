using Microsoft.UI.Dispatching;
using Titan.Engine.Memory;
using Titan.Engine.Native;
using Titan.Engine.Processes;
using Titan.Engine.Session;
using Titan.Engine.SystemTweaks;

namespace Titan.App.Services;

/// <summary>
/// In-process bridge to Titan.Engine. Everything heavy runs on the thread pool; results come back on the UI thread.
/// (Next step: the same calls go over a named pipe to the TITAN service.)
/// </summary>
public sealed class EngineHost : IDisposable
{
    private readonly DispatcherQueue _ui;
    private readonly DispatcherQueueTimer _timer;
    private readonly BoostEngine _engine = new();
    private CancellationTokenSource? _guardCts;
    private bool _guardBeforeBoost;
    private bool _busy;
    private bool _aside;

    public bool IsElevated { get; } = Privileges.IsElevated;
    public MemorySnapshot? Memory { get; private set; }
    public int Watched { get; private set; }
    public string WatchedSample { get; private set; } = "";
    public int Threads { get; }
    public int Cores { get; }
    public string PowerPlan { get; private set; } = "";
    public bool Boosted => _engine.Journal.HasOpenSession;
    public bool GuardOn => _guardCts is not null;
    public StandbyGuardOptions? GuardOptions { get; private set; }
    /// <summary>Live work of the in-game governor; null outside a boost.</summary>
    public GovernorStats? Governor { get; private set; }

    public event Action? Updated;
    public event Action<PurgeResult>? GuardPurged;
    public event Action<bool>? GuardToggled;
    public event Action<GovernorStats>? GovernorChanged;

    public EngineHost(DispatcherQueue ui)
    {
        _ui = ui;
        try
        {
            var topo = CpuTopology.Read();
            Threads = topo.CpuSets.Count;
            Cores = topo.CpuSets.Select(c => (c.Group, c.CoreIndex)).Distinct().Count();
        }
        catch { }
        _timer = ui.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    /// <summary>A session left open by a crash is undone before anything else.</summary>
    public RestoreReport? RecoverIfNeeded() =>
        IsElevated && _engine.Journal.HasOpenSession ? _engine.Restore() : null;

    public void Refresh()
    {
        if (_busy) return;
        _busy = true;
        Task.Run(() =>
        {
            var mem = MemoryLists.Read();
            var procs = ProcessOps.Snapshot();
            var policy = new ProcessPolicy();
            var tree = new HashSet<int>();
            var watched = procs.Where(p => policy.Classify(p, Environment.ProcessId, tree) is ProcessAction.Throttle or ProcessAction.Freeze).ToList();
            string sample = watched.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key ?? "";
            string plan = "";
            try { var g = PowerPlans.GetActive(); plan = PowerPlans.FriendlyName(g) ?? g.ToString(); } catch { }
            _ui.TryEnqueue(() =>
            {
                Memory = mem; Watched = watched.Count; WatchedSample = sample; PowerPlan = plan;
                _busy = false;
                Updated?.Invoke();
            });
        });
    }

    /// <summary>Engages the boost, then hands over to the governor for the rest of the session.</summary>
    public async Task<BoostReport> BoostAsync(IProgress<BoostProgress> progress, BoostLevel level, int? gamePid = null, string? gameDir = null)
    {
        // The governor owns memory during the session; the idle guardian would only fight it.
        _guardBeforeBoost = GuardOn;
        if (GuardOn) StopGuard();
        var report = await Task.Run(() => _engine.Engage(new BoostOptions { GamePid = gamePid, GameDir = gameDir, Level = level }, progress));
        Governor = _engine.Stats;
        _engine.StartGovernor(st => _ui.TryEnqueue(() => { Governor = st; GovernorChanged?.Invoke(st); }));
        return report;
    }

    public TweakCatalog Tweaks { get; } = new();

    public async Task<RestoreReport> RestoreAsync()
    {
        var r = await Task.Run(_engine.Restore);
        AfterRestore();
        return r;
    }

    public RestoreReport RestoreNow()
    {
        var r = _engine.Restore();
        AfterRestore();
        return r;
    }

    private void AfterRestore()
    {
        Governor = null;
        StepAside(false);
        if (_guardBeforeBoost) SetGuard(true);
        _guardBeforeBoost = false;
    }

    /// <summary>
    /// While the game has the screen, TITAN gets out of the way: its pages go back to Windows and the
    /// readouts slow to one refresh every 5 s. The governor keeps running.
    /// </summary>
    public void StepAside(bool aside)
    {
        if (aside == _aside) return;
        _aside = aside;
        _timer.Interval = TimeSpan.FromSeconds(aside ? 5 : 1);
        if (aside) Task.Run(() => { try { ProcessOps.TrimSelf(); } catch { } });
    }

    public Task<PurgeResult> PurgeAsync(PurgeOps ops) => Task.Run(() => MemoryPurger.Execute(ops));

    public void SetGuard(bool on)
    {
        if (Boosted) { _guardBeforeBoost = on; GuardToggled?.Invoke(on); return; }   // the governor owns memory until restore
        if (on == GuardOn) return;
        if (!on) StopGuard();
        else
        {
            _guardCts = new CancellationTokenSource();
            var guard = new StandbyGuard();
            GuardOptions = guard.Options;
            guard.Purged += r => _ui.TryEnqueue(() => GuardPurged?.Invoke(r));
            _ = Task.Run(() => guard.RunAsync(_guardCts.Token));
        }
        GuardToggled?.Invoke(on);
    }

    private void StopGuard()
    {
        _guardCts?.Cancel();
        _guardCts = null;
    }

    public void Dispose()
    {
        _timer.Stop();
        _guardCts?.Cancel();
    }
}

public static class Units
{
    public static double Gb(ulong bytes) => bytes / (1024.0 * 1024 * 1024);
    public static string GbText(ulong bytes) => $"{Gb(bytes):0.00} GB";
}
