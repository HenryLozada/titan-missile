using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Titan.Engine.Native;
using static Titan.Engine.Native.NativeMethods;

namespace Titan.Engine.Processes;

/// <summary>A process identified by PID *and* creation time, so a recycled PID is never mistaken for the original.</summary>
public readonly record struct ProcessKey(int Pid, long CreationTime);

public sealed record ProcessEntry(int Pid, int ParentPid, string Name, int SessionId);

public enum PriorityClass : uint
{
    Idle = 0x40,
    BelowNormal = 0x4000,
    Normal = 0x20,
    AboveNormal = 0x8000,
    High = 0x80,
    Realtime = 0x100,
}

public static unsafe class ProcessOps
{
    public static IReadOnlyList<ProcessEntry> Snapshot()
    {
        var list = new List<ProcessEntry>(256);
        using var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap.IsInvalid) return list;

        PROCESSENTRY32W e = default;
        e.dwSize = (uint)sizeof(PROCESSENTRY32W);
        if (!Process32FirstW(snap, &e)) return list;
        do
        {
            int pid = (int)e.th32ProcessID;
            ProcessIdToSessionId(pid, out int session);
            list.Add(new ProcessEntry(pid, (int)e.th32ParentProcessID, new string(e.szExeFile), session));
        } while (Process32NextW(snap, &e));
        return list;
    }

    public static SafeProcessHandle Open(int pid, uint access)
    {
        var h = OpenProcess(access, false, pid);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastPInvokeError();
            h.Dispose();
            throw new System.ComponentModel.Win32Exception(err, $"OpenProcess({pid})");
        }
        return h;
    }

    public static bool TryGetKey(int pid, out ProcessKey key)
    {
        key = default;
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        using (h)
        {
            if (h.IsInvalid || !GetProcessTimes(h, out long created, out _, out _, out _)) return false;
            key = new ProcessKey(pid, created);
            return true;
        }
    }

    /// <summary>Full path of the process image, or null if it can't be opened (protected / exited).</summary>
    public static string? ImagePath(int pid)
    {
        using var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h.IsInvalid) return null;
        char* buf = stackalloc char[1024];
        uint size = 1024;
        return QueryFullProcessImageName(h, 0, buf, ref size) ? new string(buf, 0, (int)size) : null;
    }

    /// <summary>True only if <paramref name="key"/> still refers to the same live process.</summary>
    public static bool IsAlive(ProcessKey key) => TryGetKey(key.Pid, out var now) && now == key;

    public static void Suspend(int pid)
    {
        using var h = Open(pid, PROCESS_SUSPEND_RESUME);
        int rc = NtSuspendProcess(h);
        if (rc != STATUS_SUCCESS) throw new NtStatusException($"NtSuspendProcess({pid})", rc);
    }

    public static void Resume(int pid)
    {
        using var h = Open(pid, PROCESS_SUSPEND_RESUME);
        int rc = NtResumeProcess(h);
        if (rc != STATUS_SUCCESS) throw new NtStatusException($"NtResumeProcess({pid})", rc);
    }

    public static PriorityClass GetPriority(int pid)
    {
        using var h = Open(pid, PROCESS_QUERY_LIMITED_INFORMATION);
        uint p = GetPriorityClass(h);
        if (p == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        return (PriorityClass)p;
    }

    public static void SetPriority(int pid, PriorityClass priority)
    {
        using var h = Open(pid, PROCESS_SET_INFORMATION);
        if (!SetPriorityClass(h, (uint)priority))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
    }

    /// <summary>
    /// EcoQoS: tells the scheduler this process may run on efficient cores / low clocks.
    /// <paramref name="enable"/> = false returns control to Windows (neither forced on nor off).
    /// </summary>
    public static void SetEcoQoS(int pid, bool enable)
    {
        using var h = Open(pid, PROCESS_SET_INFORMATION);
        var state = new PROCESS_POWER_THROTTLING_STATE
        {
            Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = enable ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0,
            StateMask = enable ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0,
        };
        if (!SetProcessInformation(h, ProcessPowerThrottling, &state, (uint)sizeof(PROCESS_POWER_THROTTLING_STATE)))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
    }

    public static uint[] GetDefaultCpuSets(int pid)
    {
        using var h = Open(pid, PROCESS_QUERY_LIMITED_INFORMATION);
        GetProcessDefaultCpuSets(h, null, 0, out uint required);
        if (required == 0) return [];
        var ids = new uint[required];
        fixed (uint* p = ids)
        {
            if (!GetProcessDefaultCpuSets(h, p, required, out _))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }
        return ids;
    }

    /// <summary>Soft affinity. An empty array clears the assignment (back to "any CPU").</summary>
    public static void SetDefaultCpuSets(int pid, uint[] ids)
    {
        using var h = Open(pid, PROCESS_SET_LIMITED_INFORMATION);
        fixed (uint* p = ids)
        {
            if (!SetProcessDefaultCpuSets(h, ids.Length == 0 ? null : p, (uint)ids.Length))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }
    }
}
