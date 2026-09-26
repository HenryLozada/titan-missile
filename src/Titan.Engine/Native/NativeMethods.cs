using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Titan.Engine.Native;

/// <summary>Raw Win32 / NT imports. Everything here is source-generated (LibraryImport) so it survives NativeAOT.</summary>
internal static unsafe partial class NativeMethods
{
    // ---------------------------------------------------------------- ntdll
    public const int SystemMemoryListInformation = 80;   // query + set (commands)
    public const int SystemFileCacheInformationEx = 81;  // system file cache working set

    public const int STATUS_SUCCESS = 0;

    [LibraryImport("ntdll.dll")]
    public static partial int NtSetSystemInformation(int infoClass, void* info, int length);

    [LibraryImport("ntdll.dll")]
    public static partial int NtQuerySystemInformation(int infoClass, void* info, int length, out int returnLength);

    [LibraryImport("ntdll.dll")]
    public static partial int NtSuspendProcess(SafeProcessHandle process);

    [LibraryImport("ntdll.dll")]
    public static partial int NtResumeProcess(SafeProcessHandle process);

    [LibraryImport("ntdll.dll")]
    public static partial uint RtlNtStatusToDosError(int status);

    // ---------------------------------------------------------------- kernel32: processes
    public const uint PROCESS_TERMINATE = 0x0001;
    public const uint PROCESS_SET_QUOTA = 0x0100;
    public const uint PROCESS_SET_INFORMATION = 0x0200;
    public const uint PROCESS_QUERY_INFORMATION = 0x0400;
    public const uint PROCESS_SUSPEND_RESUME = 0x0800;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint PROCESS_SET_LIMITED_INFORMATION = 0x2000;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint GetPriorityClass(SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetPriorityClass(SafeProcessHandle process, uint priorityClass);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ProcessIdToSessionId(int pid, out int sessionId);

    [LibraryImport("kernel32.dll", EntryPoint = "K32EmptyWorkingSet", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EmptyWorkingSet(SafeProcessHandle process);

    public const int ProcessPowerThrottling = 4;
    public const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    public const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessInformation(SafeProcessHandle process, int infoClass, void* info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessInformation(SafeProcessHandle process, int infoClass, void* info, uint size);

    // ---------------------------------------------------------------- kernel32: CPU sets / topology
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetSystemCpuSetInformation(byte* information, uint bufferLength, out uint returnedLength, nint process, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessDefaultCpuSets(SafeProcessHandle process, uint* cpuSetIds, uint cpuSetIdCount);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessDefaultCpuSets(SafeProcessHandle process, uint* cpuSetIds, uint cpuSetIdCount, out uint requiredIdCount);

    public const int RelationCache = 2;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetLogicalProcessorInformationEx(int relationshipType, byte* buffer, ref uint returnedLength);

    // ---------------------------------------------------------------- kernel32: toolhelp
    public const uint TH32CS_SNAPPROCESS = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nuint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        public fixed char szExeFile[260];
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Process32FirstW(SafeFileHandle snapshot, PROCESSENTRY32W* entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Process32NextW(SafeFileHandle snapshot, PROCESSENTRY32W* entry);

    // ---------------------------------------------------------------- kernel32: memory
    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    // ---------------------------------------------------------------- advapi32: token privileges
    public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    public const uint TOKEN_QUERY = 0x0008;
    public const uint SE_PRIVILEGE_ENABLED = 0x2;
    public const int ERROR_NOT_ALL_ASSIGNED = 1300;

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    public struct TOKEN_PRIVILEGES_1
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(nint process, uint access, out SafeAccessTokenHandle token);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AdjustTokenPrivileges(SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TOKEN_PRIVILEGES_1 newState, uint bufferLength, nint previousState, nint returnLength);

    // ---------------------------------------------------------------- powrprof
    [LibraryImport("powrprof.dll")]
    public static partial uint PowerGetActiveScheme(nint rootPowerKey, Guid** activePolicyGuid);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerSetActiveScheme(nint rootPowerKey, in Guid schemeGuid);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerDuplicateScheme(nint rootPowerKey, in Guid sourceScheme, Guid** destinationScheme);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerWriteFriendlyName(nint rootPowerKey, in Guid scheme, Guid* subGroup, Guid* setting, byte* buffer, uint bufferSize);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerReadFriendlyName(nint rootPowerKey, in Guid scheme, Guid* subGroup, Guid* setting, byte* buffer, ref uint bufferSize);

    // Undocumented but stable since Win10 1709: the "power mode" slider (overlay scheme).
    [LibraryImport("powrprof.dll")]
    public static partial uint PowerGetEffectiveOverlayScheme(out Guid overlayScheme);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerSetActiveOverlayScheme(in Guid overlayScheme);

    [LibraryImport("kernel32.dll")]
    public static partial nint LocalFree(void* mem);

    // ---------------------------------------------------------------- shell32
    [LibraryImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsUserAnAdmin();
}
