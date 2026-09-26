using System.Runtime.InteropServices;
using Titan.Engine.Native;
using static Titan.Engine.Native.NativeMethods;

namespace Titan.Engine.Memory;

/// <summary>
/// Physical memory broken down the way RAMMap shows it.
/// <see cref="FreeBytes"/> is the real free list (Free + Zero) — NOT "available", which also counts Standby.
/// </summary>
public sealed record MemorySnapshot(
    ulong TotalBytes,
    ulong AvailableBytes,
    ulong FreeBytes,
    ulong ZeroBytes,
    ulong StandbyBytes,
    ulong StandbyLowPriorityBytes,
    ulong ModifiedBytes,
    bool Detailed)
{
    public ulong InUseBytes => TotalBytes - AvailableBytes - ModifiedBytes;
}

public static unsafe class MemoryLists
{
    // SYSTEM_MEMORY_LIST_INFORMATION (x64: ULONG_PTR == 8 bytes)
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryListInfo
    {
        public ulong ZeroPageCount;
        public ulong FreePageCount;
        public ulong ModifiedPageCount;
        public ulong ModifiedNoWritePageCount;
        public ulong BadPageCount;
        public fixed ulong PageCountByPriority[8];
        public fixed ulong RepurposedPagesByPriority[8];
        public ulong ModifiedPageCountPageFile;
    }

    /// <summary>
    /// Reads the memory lists. Needs SeProfileSingleProcessPrivilege (admin); without it falls back to
    /// GlobalMemoryStatusEx and reports <see cref="MemorySnapshot.Detailed"/> = false.
    /// </summary>
    public static MemorySnapshot Read()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)sizeof(MEMORYSTATUSEX) };
        GlobalMemoryStatusEx(ref status);

        Privileges.Enable(Privileges.ProfileSingleProcess);
        MemoryListInfo info;
        int rc = NtQuerySystemInformation(SystemMemoryListInformation, &info, sizeof(MemoryListInfo), out _);
        if (rc != STATUS_SUCCESS)
        {
            return new MemorySnapshot(status.ullTotalPhys, status.ullAvailPhys, 0, 0, 0, 0, 0, Detailed: false);
        }

        ulong page = (ulong)Environment.SystemPageSize;
        ulong standby = 0;
        for (int i = 0; i < 8; i++) standby += info.PageCountByPriority[i];

        return new MemorySnapshot(
            TotalBytes: status.ullTotalPhys,
            AvailableBytes: status.ullAvailPhys,
            FreeBytes: (info.FreePageCount + info.ZeroPageCount) * page,
            ZeroBytes: info.ZeroPageCount * page,
            StandbyBytes: standby * page,
            StandbyLowPriorityBytes: info.PageCountByPriority[0] * page,
            ModifiedBytes: info.ModifiedPageCount * page,
            Detailed: true);
    }
}
