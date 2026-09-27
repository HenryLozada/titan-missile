using Titan.Engine.Memory;
using Titan.Engine.Processes;
using Titan.Engine.Session;

namespace Titan.Engine.Tests;

public sealed class MemoryPressureTests
{
    private const ulong GB = 1024UL * 1024 * 1024;
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddHours(1);
    private static readonly DateTimeOffset Long = DateTimeOffset.MinValue;

    private static MemorySnapshot Mem(double totalGb, double freeGb, double standbyGb) =>
        new((ulong)(totalGb * GB), 0, (ulong)(freeGb * GB), 0, (ulong)(standbyGb * GB), 0, 0, Detailed: true);

    [Theory]
    [InlineData(5.0, 6.0, PressureAction.None)]       // 16 GB, LÍMITE: plenty free
    [InlineData(2.5, 6.0, PressureAction.Surgical)]   // below 1/5 of RAM → only background pages
    [InlineData(1.0, 6.0, PressureAction.Full)]       // below 1/12 of RAM with cache to reclaim → ISLC purge
    [InlineData(1.0, 0.1, PressureAction.Surgical)]   // nothing on standby: a full purge would be noise
    public void Max_level_tiers_on_16_GB(double free, double standby, PressureAction expected)
    {
        var o = PressureOptions.For(16 * GB, BoostLevel.Max);
        Assert.Equal(expected, MemoryPressure.Decide(Mem(16, free, standby), o, Now, Long, Long));
    }

    [Fact]
    public void Balanced_waits_for_real_pressure()
    {
        var o = PressureOptions.For(16 * GB, BoostLevel.Balanced);
        Assert.Equal(PressureAction.None, MemoryPressure.Decide(Mem(16, 2.5, 6), o, Now, Long, Long));
        Assert.Equal(PressureAction.Full, MemoryPressure.Decide(Mem(16, 0.8, 6), o, Now, Long, Long));
    }

    [Fact]
    public void Cooldowns_stop_purge_storms()
    {
        var o = PressureOptions.For(16 * GB, BoostLevel.Max);
        var low = Mem(16, 1.0, 6);
        // just did a full purge: surgical is on cooldown too
        Assert.Equal(PressureAction.None, MemoryPressure.Decide(low, o, Now, Now.AddSeconds(-3), Now.AddSeconds(-3)));
        // full still cooling down, surgical ready
        Assert.Equal(PressureAction.Surgical, MemoryPressure.Decide(low, o, Now, Now.AddSeconds(-10), Now.AddSeconds(-10)));
        // both ready
        Assert.Equal(PressureAction.Full, MemoryPressure.Decide(low, o, Now, Now.AddSeconds(-30), Now.AddSeconds(-30)));
    }

    [Theory]
    [InlineData(8, 2.0)]    // floor
    [InlineData(32, 6.4)]
    [InlineData(128, 8.0)]  // ceiling
    public void Surgical_threshold_scales_with_ram(double totalGb, double expectedGb)
    {
        var o = PressureOptions.For((ulong)(totalGb * GB), BoostLevel.Max);
        Assert.Equal(expectedGb, o.SurgicalBelow / (double)GB, 1);
    }

    [Fact]
    public void No_counters_no_action()
    {
        var o = PressureOptions.For(16 * GB, BoostLevel.Max);
        Assert.Equal(PressureAction.None, MemoryPressure.Decide(Mem(16, 0.1, 10) with { Detailed = false }, o, Now, Long, Long));
    }

    [Theory]
    [InlineData(4, 1.0)]
    [InlineData(16, 2.0)]
    [InlineData(64, 4.0)]
    public void Idle_guardian_threshold_scales_with_ram(double totalGb, double expectedGb) =>
        Assert.Equal(expectedGb, StandbyGuardOptions.ForRam((ulong)(totalGb * GB)).FreeBelowBytes / (double)GB, 2);
}

public sealed class GameMembershipTests
{
    [Theory]
    [InlineData(@"D:\Games\Foo\bin\Foo.exe", @"D:\Games\Foo", true)]
    [InlineData(@"D:\Games\Foo\Foo.exe", @"D:/Games/Foo/", true)]
    [InlineData(@"D:\Games\FooBar\x.exe", @"D:\Games\Foo", false)]   // shared prefix is not "inside"
    [InlineData(null, @"D:\Games\Foo", false)]
    public void Is_under_folder(string? path, string dir, bool expected) =>
        Assert.Equal(expected, ProcessPolicy.IsUnder(path, dir));

    [Theory]
    [InlineData("UnityCrashHandler64.exe", true)]
    [InlineData("CrashReportClient.exe", true)]
    [InlineData("unins000.exe", true)]
    [InlineData("Game-Win64-Shipping.exe", false)]
    [InlineData("eldenring.exe", false)]
    public void Helpers_are_not_the_game(string exe, bool expected) =>
        Assert.Equal(expected, ProcessPolicy.IsGameHelper(exe));

    [Fact]
    public void Voice_apps_are_throttled_but_marked_responsive()
    {
        var discord = new ProcessEntry(10, 1, "Discord.exe", 1);
        Assert.Equal(ProcessAction.Throttle, new ProcessPolicy().Classify(discord, 1, new HashSet<int>()));
        Assert.Contains("Discord.exe", ProcessPolicy.Responsive);
        Assert.DoesNotContain("chrome.exe", ProcessPolicy.Responsive);
    }
}

public sealed class JournalNewEntriesTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "titan-tests", Guid.NewGuid() + ".json");

    [Fact]
    public void Governor_entries_survive_a_crash_round_trip()
    {
        var j = new Journal(_path);
        j.Begin(7, "game.exe");
        j.Record(new HighQoSApplied(7, 1, "game.exe"));
        j.Record(new GpuPriorityChanged(7, 1, "game.exe", 2));
        j.Record(new MemoryPriorityChanged(9, 2, "chrome.exe", 5));
        j.Record(new IoPriorityChanged(9, 2, "chrome.exe", 2));

        var doc = new Journal(_path).Load()!;
        Assert.Collection(doc.Entries,
            e => Assert.IsType<HighQoSApplied>(e),
            e => Assert.Equal(2, Assert.IsType<GpuPriorityChanged>(e).Previous),
            e => Assert.Equal(5u, Assert.IsType<MemoryPriorityChanged>(e).Previous),
            e => Assert.Equal(2, Assert.IsType<IoPriorityChanged>(e).Previous));
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }
}
