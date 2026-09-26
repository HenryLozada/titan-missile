using System.Runtime.InteropServices;
using static Titan.Engine.Native.NativeMethods;

namespace Titan.Engine.Native;

public static class Privileges
{
    public const string ProfileSingleProcess = "SeProfileSingleProcessPrivilege"; // memory list commands
    public const string IncreaseQuota = "SeIncreaseQuotaPrivilege";               // system file cache working set
    public const string Debug = "SeDebugPrivilege";                               // open processes of other users

    public static bool IsElevated => IsUserAnAdmin();

    /// <summary>Enables a privilege on the current process token. Returns false if the token does not hold it.</summary>
    public static bool Enable(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
            return false;
        using (token)
        {
            if (!LookupPrivilegeValue(null, name, out var luid))
                return false;

            var tp = new TOKEN_PRIVILEGES_1 { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            if (!AdjustTokenPrivileges(token, false, ref tp, (uint)Marshal.SizeOf<TOKEN_PRIVILEGES_1>(), 0, 0))
                return false;

            // AdjustTokenPrivileges "succeeds" even when the privilege is missing; the real answer is in last-error.
            return Marshal.GetLastPInvokeError() != ERROR_NOT_ALL_ASSIGNED;
        }
    }
}

public sealed class NtStatusException(string operation, int status)
    : Exception($"{operation} falló: NTSTATUS 0x{status:X8} ({new System.ComponentModel.Win32Exception((int)RtlNtStatusToDosError(status)).Message})")
{
    public int Status { get; } = status;
}
