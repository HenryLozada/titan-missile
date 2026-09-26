namespace Titan.Engine.Memory;

public sealed record StandbyGuardOptions
{
    /// <summary>Purge only when the real free list drops below this.</summary>
    public ulong FreeBelowBytes { get; init; } = 1024UL * 1024 * 1024;       // 1 GB
    /// <summary>…and only if there is enough standby to be worth it.</summary>
    public ulong StandbyAboveBytes { get; init; } = 1024UL * 1024 * 1024;    // 1 GB
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// ISLC-style watchdog: purges the standby list only when memory pressure is real,
/// instead of purging blindly on a timer (which just throws away useful cache).
/// </summary>
public sealed class StandbyGuard
{
    private readonly StandbyGuardOptions _options;
    private readonly Func<MemorySnapshot> _read;
    private readonly Func<PurgeResult> _purge;
    private readonly Func<DateTimeOffset> _now;
    private DateTimeOffset _lastPurge = DateTimeOffset.MinValue;

    public event Action<PurgeResult>? Purged;
    public int PurgeCount { get; private set; }

    public StandbyGuard(StandbyGuardOptions? options = null)
        : this(options ?? new(), MemoryLists.Read, () => MemoryPurger.Execute(PurgeOps.Boost), () => DateTimeOffset.UtcNow) { }

    internal StandbyGuard(StandbyGuardOptions options, Func<MemorySnapshot> read, Func<PurgeResult> purge, Func<DateTimeOffset> now)
    {
        _options = options; _read = read; _purge = purge; _now = now;
    }

    /// <summary>One evaluation. Returns the purge result if it purged.</summary>
    public PurgeResult? Tick()
    {
        var now = _now();
        if (now - _lastPurge < _options.Cooldown) return null;

        var mem = _read();
        if (!mem.Detailed) return null;
        if (mem.FreeBytes >= _options.FreeBelowBytes || mem.StandbyBytes <= _options.StandbyAboveBytes) return null;

        var result = _purge();
        _lastPurge = now;
        PurgeCount++;
        Purged?.Invoke(result);
        return result;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct)) Tick();
        }
        catch (OperationCanceledException) { }
    }
}
