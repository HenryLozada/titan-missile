using System.Runtime.InteropServices;
using static Titan.Engine.Native.NativeMethods;

namespace Titan.Engine.Processes;

public sealed record CpuSetInfo(uint Id, ushort Group, byte LogicalIndex, byte CoreIndex, byte LastLevelCacheIndex, byte EfficiencyClass);

public sealed record L3Cache(ushort Group, ulong Mask, uint SizeBytes)
{
    public bool Covers(ushort group, byte logicalIndex) => group == Group && (Mask & (1UL << logicalIndex)) != 0;
}

public enum GameCoreStrategy { None, HybridPerformanceCores, LargestL3Cache }

public sealed record GameCorePlan(GameCoreStrategy Strategy, uint[] CpuSetIds, string Reason);

public sealed record CpuTopology(IReadOnlyList<CpuSetInfo> CpuSets, IReadOnlyList<L3Cache> L3Caches)
{
    public static unsafe CpuTopology Read()
    {
        // --- CPU sets
        var sets = new List<CpuSetInfo>();
        GetSystemCpuSetInformation(null, 0, out uint len, GetCurrentProcess(), 0);
        var buf = new byte[len];
        fixed (byte* p = buf)
        {
            if (GetSystemCpuSetInformation(p, len, out len, GetCurrentProcess(), 0))
            {
                // SYSTEM_CPU_SET_INFORMATION: Size@0 Type@4 Id@8 Group@12 LogicalIndex@14 CoreIndex@15
                // LastLevelCacheIndex@16 NumaNode@17 EfficiencyClass@18
                for (uint off = 0; off < len;)
                {
                    byte* e = p + off;
                    uint size = *(uint*)e;
                    if (size == 0) break;
                    if (*(uint*)(e + 4) == 0) // CpuSetInformation
                        sets.Add(new CpuSetInfo(*(uint*)(e + 8), *(ushort*)(e + 12), e[14], e[15], e[16], e[18]));
                    off += size;
                }
            }
        }

        // --- L3 caches
        var caches = new List<L3Cache>();
        uint clen = 0;
        GetLogicalProcessorInformationEx(RelationCache, null, ref clen);
        var cbuf = new byte[clen];
        fixed (byte* p = cbuf)
        {
            if (GetLogicalProcessorInformationEx(RelationCache, p, ref clen))
            {
                // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX: Relationship@0 Size@4 CACHE_RELATIONSHIP@8
                // CACHE_RELATIONSHIP: Level@0 CacheSize@4 GroupCount@30 GroupMask{Mask@32 Group@40}
                for (uint off = 0; off < clen;)
                {
                    byte* e = p + off;
                    uint size = *(uint*)(e + 4);
                    if (size == 0) break;
                    byte* c = e + 8;
                    if (c[0] == 3)
                        caches.Add(new L3Cache(*(ushort*)(c + 40), *(ulong*)(c + 32), *(uint*)(c + 4)));
                    off += size;
                }
            }
        }
        return new CpuTopology(sets, caches);
    }

    public bool IsHybrid => CpuSets.Select(c => c.EfficiencyClass).Distinct().Count() > 1;

    /// <summary>
    /// Decides where the game should run:
    /// Intel hybrid → P-cores only (highest EfficiencyClass);
    /// AMD dual-CCD with asymmetric L3 (X3D) → the V-Cache CCD;
    /// homogeneous CPU → leave the scheduler alone (pinning would only hurt).
    /// </summary>
    public GameCorePlan PlanForGame()
    {
        if (CpuSets.Count == 0) return new(GameCoreStrategy.None, [], "Sin información de CPU sets.");

        if (IsHybrid)
        {
            byte top = CpuSets.Max(c => c.EfficiencyClass);
            var p = CpuSets.Where(c => c.EfficiencyClass == top).Select(c => c.Id).ToArray();
            return new(GameCoreStrategy.HybridPerformanceCores, p,
                $"CPU híbrida: juego en {p.Length} hilos de P-cores, E-cores libres para el resto.");
        }

        var distinct = L3Caches.Select(c => c.SizeBytes).Distinct().ToList();
        if (L3Caches.Count > 1 && distinct.Count > 1)
        {
            var best = L3Caches.MaxBy(c => c.SizeBytes)!;
            var ids = CpuSets.Where(c => best.Covers(c.Group, c.LogicalIndex)).Select(c => c.Id).ToArray();
            if (ids.Length > 0)
                return new(GameCoreStrategy.LargestL3Cache, ids,
                    $"CCD con {best.SizeBytes / (1024 * 1024)} MB de L3 (3D V-Cache): juego en {ids.Length} hilos.");
        }

        return new(GameCoreStrategy.None, [], "CPU homogénea: el planificador de Windows ya reparte bien, no se fija nada.");
    }
}
