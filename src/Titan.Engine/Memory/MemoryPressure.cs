namespace Titan.Engine.Memory;

public enum BoostLevel
{
    /// <summary>Conservative: fewer services, purges only under real pressure.</summary>
    Balanced,
    /// <summary>LÍMITE: everything reversible we know that measurably helps, and memory kept clear the whole session.</summary>
    Max,
}

public enum PressureAction
{
    None,
    /// <summary>
    /// Surgical: trim the tamed background apps (their pages drop to very-low-priority standby, first in line for reuse)
    /// and purge priority-0 standby. The game's working set and its priority-5 cache are untouched.
    /// </summary>
    Surgical,
    /// <summary>ISLC: purge the whole standby list. Only when the free list is truly running out.</summary>
    Full,
}

/// <summary>Thresholds scale with installed RAM, so 16 GB and 64 GB machines both get sensible behavior.</summary>
public sealed record PressureOptions(ulong SurgicalBelow, ulong FullBelow, ulong MinReclaimable, TimeSpan SurgicalCooldown, TimeSpan FullCooldown)
{
    private const ulong Gb = 1024UL * 1024 * 1024;

    public static PressureOptions For(ulong totalRam, BoostLevel level) => level == BoostLevel.Max
        ? new(Clamp(totalRam / 5, 2 * Gb, 8 * Gb), Clamp(totalRam / 12, 1 * Gb, 4 * Gb), 256UL << 20,
              TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(25))
        : new(Clamp(totalRam / 10, 1 * Gb, 4 * Gb), 1 * Gb, 1 * Gb,
              TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(45));

    private static ulong Clamp(ulong v, ulong lo, ulong hi) => Math.Min(hi, Math.Max(lo, v));
}

public static class MemoryPressure
{
    /// <summary>
    /// Full wins over surgical when both are due. Nothing happens if there is nothing reclaimable —
    /// purging an empty standby list is just noise.
    /// </summary>
    public static PressureAction Decide(MemorySnapshot m, PressureOptions o, DateTimeOffset now, DateTimeOffset lastSurgical, DateTimeOffset lastFull)
    {
        if (!m.Detailed) return PressureAction.None;
        if (m.FreeBytes < o.FullBelow && m.StandbyBytes > o.MinReclaimable && now - lastFull >= o.FullCooldown)
            return PressureAction.Full;
        if (m.FreeBytes < o.SurgicalBelow && now - lastSurgical >= o.SurgicalCooldown)
            return PressureAction.Surgical;
        return PressureAction.None;
    }
}
