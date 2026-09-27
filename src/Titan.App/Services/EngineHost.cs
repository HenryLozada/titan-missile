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
    private bool _busy;

    public bool IsElevated { get; } = Privileges.IsElevated;
    public MemorySnapshot? Memory { get; private set; }
    public int Watched { get; private set; }
    public string WatchedSample { get; private set; } = "";
    public int Threads { get; }
    public int Cores { get; }
    public string PowerPlan { get; private set; } = "";
    public bool Boosted => _engine.Journal.HasOpenSession;
    public bool GuardOn => _guardCts is not null;

    public event Action? Updated;
    public event Action<PurgeResult>? GuardPurged;

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

    public Task<BoostReport> BoostAsync(IProgress<BoostProgress> progress, int? gamePid = null) =>
        Task.Run(() => _engine.Engage(new BoostOptions { GamePid = gamePid }, progress));

    public TweakCatalog Tweaks { get; } = new();

    public Task<RestoreReport> RestoreAsync() => Task.Run(_engine.Restore);

    public RestoreReport RestoreNow() => _engine.Restore();

    public Task<PurgeResult> PurgeAsync(PurgeOps ops) => Task.Run(() => MemoryPurger.Execute(ops));

    public void SetGuard(bool on)
    {
        if (on == GuardOn) return;
        if (!on) { _guardCts!.Cancel(); _guardCts = null; return; }
        _guardCts = new CancellationTokenSource();
        var guard = new StandbyGuard();
        guard.Purged += r => _ui.TryEnqueue(() => GuardPurged?.Invoke(r));
        _ = Task.Run(() => guard.RunAsync(_guardCts.Token));
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
