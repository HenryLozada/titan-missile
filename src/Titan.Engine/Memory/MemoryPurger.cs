using System.Runtime.InteropServices;
using Titan.Engine.Native;
using static Titan.Engine.Native.NativeMethods;

namespace Titan.Engine.Memory;

[Flags]
public enum PurgeOps
{
    None = 0,
    /// <summary>Trim every process working set. Off by default: pages come back as hard faults = stutter.</summary>
    WorkingSets = 1 << 0,
    /// <summary>Trim the system file cache working set (RAMMap "Empty System Working Set").</summary>
    SystemWorkingSet = 1 << 1,
    /// <summary>Write dirty pages to disk so they become Standby (RAMMap "Empty Modified Page List").</summary>
    ModifiedList = 1 << 2,
    /// <summary>Priority-0 standby only: the cheapest, safest purge.</summary>
    StandbyLowPriority = 1 << 3,
    /// <summary>Whole standby list (RAMMap "Empty Standby List").</summary>
    Standby = 1 << 4,

    /// <summary>What BOOST uses: safe and does not cause re-fault stutter.</summary>
    Boost = StandbyLowPriority | Standby,
    /// <summary>Full RAMMap sweep, in the order that doesn't undo itself.</summary>
    All = WorkingSets | SystemWorkingSet | ModifiedList | StandbyLowPriority | Standby,
}

public sealed record PurgeStep(PurgeOps Op, bool Ok, string? Error);

public sealed record PurgeResult(MemorySnapshot Before, MemorySnapshot After, IReadOnlyList<PurgeStep> Steps, TimeSpan Elapsed)
{
    /// <summary>Growth of the real free list. Can be negative if something allocated meanwhile.</summary>
    public long FreedBytes => (long)After.FreeBytes - (long)Before.FreeBytes;
}

public static unsafe class MemoryPurger
{
    private enum MemoryListCommand
    {
        EmptyWorkingSets = 2,
        FlushModifiedList = 3,
        PurgeStandbyList = 4,
        PurgeLowPriorityStandbyList = 5,
    }

    // SYSTEM_FILECACHE_INFORMATION (x64)
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemFileCacheInformation
    {
        public nuint CurrentSize;
        public nuint PeakSize;
        public uint PageFaultCount;
        public nuint MinimumWorkingSet;
        public nuint MaximumWorkingSet;
        public nuint CurrentSizeIncludingTransitionInPages;
        public nuint PeakSizeIncludingTransitionInPages;
        public uint TransitionRePurposeCount;
        public uint Flags;
    }

    /// <summary>
    /// Runs the requested operations in the only order that doesn't undo itself:
    /// trim working sets → flush modified → purge standby. (Purging standby first and then trimming
    /// just refills standby — the bug the Python TITAN had.)
    /// </summary>
    public static PurgeResult Execute(PurgeOps ops)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Privileges.Enable(Privileges.ProfileSingleProcess);
        Privileges.Enable(Privileges.IncreaseQuota);

        var before = MemoryLists.Read();
        var steps = new List<PurgeStep>();

        void Run(PurgeOps op, Action action)
        {
            if (!ops.HasFlag(op)) return;
            try { action(); steps.Add(new PurgeStep(op, true, null)); }
            catch (Exception ex) { steps.Add(new PurgeStep(op, false, ex.Message)); }
        }

        Run(PurgeOps.WorkingSets, () => Command(MemoryListCommand.EmptyWorkingSets));
        Run(PurgeOps.SystemWorkingSet, EmptySystemFileCache);
        Run(PurgeOps.ModifiedList, () => Command(MemoryListCommand.FlushModifiedList));
        Run(PurgeOps.StandbyLowPriority, () => Command(MemoryListCommand.PurgeLowPriorityStandbyList));
        Run(PurgeOps.Standby, () => Command(MemoryListCommand.PurgeStandbyList));

        var after = MemoryLists.Read();
        return new PurgeResult(before, after, steps, sw.Elapsed);
    }

    private static void Command(MemoryListCommand command)
    {
        int cmd = (int)command;
        int rc = NtSetSystemInformation(SystemMemoryListInformation, &cmd, sizeof(int));
        if (rc != STATUS_SUCCESS) throw new NtStatusException(command.ToString(), rc);
    }

    private static void EmptySystemFileCache()
    {
        var info = new SystemFileCacheInformation
        {
            MinimumWorkingSet = nuint.MaxValue,
            MaximumWorkingSet = nuint.MaxValue,
        };
        int rc = NtSetSystemInformation(SystemFileCacheInformationEx, &info, sizeof(SystemFileCacheInformation));
        if (rc != STATUS_SUCCESS) throw new NtStatusException("EmptySystemWorkingSet", rc);
    }
}
