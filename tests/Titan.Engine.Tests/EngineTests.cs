using Titan.Engine.Memory;
using Titan.Engine.Processes;
using Titan.Engine.Session;
using Titan.Engine.SystemTweaks;

namespace Titan.Engine.Tests;

public sealed class JournalTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "titan-tests", Guid.NewGuid() + ".json");

    private sealed class RecordingReverter : IChangeReverter
    {
        public List<JournalEntry> Reverted { get; } = [];
        public Func<JournalEntry, bool> Fail { get; init; } = _ => false;
        public void Revert(JournalEntry e)
        {
            if (Fail(e)) throw new InvalidOperationException("boom");
            Reverted.Add(e);
        }
    }

    [Fact]
    public void Crash_mid_session_is_recovered_by_a_fresh_process_in_reverse_order()
    {
        var first = new Journal(_path);
        first.Begin(1234, "game.exe");
        first.Record(new PowerPlanChanged(Guid.Empty));
        first.Record(new ServiceStopped("SysMain"));
        first.Record(new ProcessFrozen(42, 99, "OneDrive.exe"));
        // "crash": first instance is simply abandoned

        var reverter = new RecordingReverter();
        var engine = new BoostEngine(new Journal(_path), reverter);
        Assert.True(engine.Journal.HasOpenSession);

        var report = engine.Restore();

        Assert.Equal(3, report.Reverted);
        Assert.Empty(report.Failures);
        Assert.IsType<ProcessFrozen>(reverter.Reverted[0]);
        Assert.IsType<ServiceStopped>(reverter.Reverted[1]);
        Assert.IsType<PowerPlanChanged>(reverter.Reverted[2]);
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void A_failing_undo_does_not_stop_the_others()
    {
        var j = new Journal(_path);
        j.Begin(null, null);
        j.Record(new ServiceStopped("A"));
        j.Record(new ServiceStopped("B"));
        j.Record(new ServiceStopped("C"));

        var reverter = new RecordingReverter { Fail = e => e is ServiceStopped { Name: "B" } };
        var report = new BoostEngine(j, reverter).Restore();

        Assert.Equal(2, report.Reverted);
        Assert.Single(report.Failures);
        Assert.Contains("B", report.Failures[0]);
    }

    [Fact]
    public void Forgotten_entries_are_not_undone_and_everything_round_trips_through_json()
    {
        var j = new Journal(_path);
        j.Begin(7, "x.exe");
        var keep = new CpuSetsChanged(7, 1, "x.exe", [256, 257]);
        var drop = new EcoQoSApplied(8, 2, "y.exe");
        j.Record(keep);
        j.Record(drop);
        j.Record(new RegistryChanged(new DwordValue("HKLM", RegistryValues.MmcssKey, "SystemResponsiveness", null)));
        j.Record(new PriorityChanged(7, 1, "x.exe", PriorityClass.AboveNormal));
        j.Forget(drop);

        var loaded = new Journal(_path).Load()!;
        Assert.Equal(3, loaded.Entries.Count);
        Assert.Equal([256u, 257u], ((CpuSetsChanged)loaded.Entries[0]).Previous);
        Assert.Null(((RegistryChanged)loaded.Entries[1]).Previous.Value);
        Assert.Equal(PriorityClass.AboveNormal, ((PriorityChanged)loaded.Entries[2]).Previous);
        Assert.Equal("x.exe", loaded.GameName);
    }

    [Fact]
    public void Cannot_start_a_second_session_over_an_open_one()
    {
        new Journal(_path).Begin(null, null);
        Assert.Throws<InvalidOperationException>(() => new Journal(_path).Begin(null, null));
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}

public sealed class StandbyGuardTests
{
    private const ulong GB = 1024UL * 1024 * 1024;

    private static MemorySnapshot Mem(double freeGb, double standbyGb) =>
        new(16 * GB, 0, (ulong)(freeGb * GB), 0, (ulong)(standbyGb * GB), 0, 0, Detailed: true);

    private static PurgeResult Fake(MemorySnapshot m) => new(m, m, [], TimeSpan.Zero);

    [Theory]
    [InlineData(0.5, 6.0, true)]   // low free, lots of cache → purge
    [InlineData(4.0, 6.0, false)]  // plenty free → leave the cache alone
    [InlineData(0.5, 0.5, false)]  // low free but nothing to purge
    public void Purges_only_under_real_pressure(double free, double standby, bool expected)
    {
        var mem = Mem(free, standby);
        var guard = new StandbyGuard(new StandbyGuardOptions(), () => mem, () => Fake(mem), () => DateTimeOffset.UnixEpoch);
        Assert.Equal(expected, guard.Tick() is not null);
    }

    [Fact]
    public void Respects_cooldown()
    {
        var mem = Mem(0.2, 8);
        var now = DateTimeOffset.UnixEpoch;
        var guard = new StandbyGuard(new StandbyGuardOptions { Cooldown = TimeSpan.FromSeconds(30) }, () => mem, () => Fake(mem), () => now);

        Assert.NotNull(guard.Tick());
        now += TimeSpan.FromSeconds(10);
        Assert.Null(guard.Tick());
        now += TimeSpan.FromSeconds(25);
        Assert.NotNull(guard.Tick());
        Assert.Equal(2, guard.PurgeCount);
    }

    [Fact]
    public void Does_nothing_without_detailed_counters()
    {
        var mem = Mem(0.1, 10) with { Detailed = false };
        var guard = new StandbyGuard(new StandbyGuardOptions(), () => mem, () => Fake(mem), () => DateTimeOffset.UnixEpoch);
        Assert.Null(guard.Tick());
    }
}

public sealed class CpuTopologyTests
{
    private static CpuSetInfo Cpu(uint id, byte logical, byte core, byte eff = 0) => new(id, 0, logical, core, 0, eff);

    [Fact]
    public void Hybrid_intel_pins_game_to_p_cores()
    {
        // 2 P-cores with HT (class 1) + 4 E-cores (class 0)
        var sets = new List<CpuSetInfo>
        {
            Cpu(256, 0, 0, 1), Cpu(257, 1, 0, 1), Cpu(258, 2, 1, 1), Cpu(259, 3, 1, 1),
            Cpu(260, 4, 2), Cpu(261, 5, 3), Cpu(262, 6, 4), Cpu(263, 7, 5),
        };
        var plan = new CpuTopology(sets, [new L3Cache(0, 0xFF, 30 << 20)]).PlanForGame();
        Assert.Equal(GameCoreStrategy.HybridPerformanceCores, plan.Strategy);
        Assert.Equal([256u, 257u, 258u, 259u], plan.CpuSetIds);
    }

    [Fact]
    public void Amd_x3d_pins_game_to_the_vcache_ccd()
    {
        var sets = Enumerable.Range(0, 8).Select(i => Cpu((uint)(256 + i), (byte)i, (byte)(i / 2))).ToList();
        var caches = new List<L3Cache> { new(0, 0x0F, 96 << 20), new(0, 0xF0, 32 << 20) };
        var plan = new CpuTopology(sets, caches).PlanForGame();
        Assert.Equal(GameCoreStrategy.LargestL3Cache, plan.Strategy);
        Assert.Equal([256u, 257u, 258u, 259u], plan.CpuSetIds);
    }

    [Fact]
    public void Homogeneous_cpu_is_left_alone()
    {
        var sets = Enumerable.Range(0, 8).Select(i => Cpu((uint)(256 + i), (byte)i, (byte)(i / 2))).ToList();
        var caches = new List<L3Cache> { new(0, 0x0F, 32 << 20), new(0, 0xF0, 32 << 20) };
        var plan = new CpuTopology(sets, caches).PlanForGame();
        Assert.Equal(GameCoreStrategy.None, plan.Strategy);
        Assert.Empty(plan.CpuSetIds);
    }
}

public sealed class ProcessPolicyTests
{
    private static readonly List<ProcessEntry> Procs =
    [
        new(1000, 900, "explorer.exe", 1),
        new(1100, 1000, "steam.exe", 1),
        new(1200, 1100, "game.exe", 1),
        new(1300, 1200, "CrashReporter.exe", 1),
        new(1400, 1000, "Discord.exe", 1),
        new(1500, 1000, "OneDrive.exe", 1),
        new(1600, 1000, "EasyAntiCheat.exe", 1),
        new(1700, 600, "OneDrive.exe", 0), // session 0 copy
        new(1800, 1000, "notepad.exe", 1),
    ];

    [Fact]
    public void Game_tree_covers_launcher_chain_and_children()
    {
        var tree = ProcessPolicy.GameTree(Procs, 1200);
        Assert.Contains(1100, tree); // steam launched it
        Assert.Contains(1200, tree);
        Assert.Contains(1300, tree); // spawned by the game
        Assert.DoesNotContain(1400, tree);
    }

    [Theory]
    [InlineData(1400, ProcessAction.Throttle)]   // Discord: throttled, never frozen (voice calls)
    [InlineData(1500, ProcessAction.Freeze)]
    [InlineData(1600, ProcessAction.Protected)]  // anti-cheat
    [InlineData(1700, ProcessAction.Protected)]  // session 0
    [InlineData(1300, ProcessAction.Protected)]  // part of the game tree
    [InlineData(1800, ProcessAction.None)]
    public void Classifies_safely(int pid, ProcessAction expected)
    {
        var tree = ProcessPolicy.GameTree(Procs, 1200);
        var p = Procs.Single(x => x.Pid == pid);
        Assert.Equal(expected, new ProcessPolicy().Classify(p, selfPid: 1, tree));
    }
}
