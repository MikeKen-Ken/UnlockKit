using System.Runtime.InteropServices;
using UnlockKit.Native;

namespace UnlockKit.Core;

internal enum KillOutcome
{
    Succeeded,
    AccessDenied,
    ProtectedProcess,
    Zombie,
    NotFound
}

internal sealed record KillResult(KillOutcome Outcome, string Detail, int Win32Error);

internal sealed class ProcessProbe
{
    private static readonly Dictionary<int, string> NameCache = new();
    private static bool _privilegesEnabled;

    public static string NameOf(int pid)
    {
        if (NameCache.TryGetValue(pid, out var cached)) return cached;

        var name = "?";
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            name = p.ProcessName + ".exe";
        }
        catch
        {
            if (pid == 4) name = "System";
            else if (pid == 0) name = "Idle";
        }

        NameCache[pid] = name;
        return name;
    }

    /// <summary>
    /// Protection level byte: low 3 bits are the type (0 = none, 1 = light, 2 = full).
    /// Anything above 0 means the kernel will refuse TerminateProcess no matter what
    /// token you hold - that is PPL, not a permissions bug.
    /// </summary>
    public static byte ProtectionLevel(int pid)
    {
        var proc = Native.Native.OpenProcess(Native.Native.ProcessQueryLimitedInformation, false, pid);
        if (proc == IntPtr.Zero) return 0;

        try
        {
            var info = new ProcessProtectionInformation();
            var status = Native.Native.NtQueryInformationProcess(
                proc, Native.Native.ProcessProtectionInformationClass, ref info, 2, out _);
            return status == NtStatus.Success ? info.Level : (byte)0;
        }
        catch
        {
            return 0;
        }
        finally
        {
            Native.Native.CloseHandle(proc);
        }
    }

    public static bool IsProtected(int pid) => (ProtectionLevel(pid) & 0x7) != 0;

    /// <summary>SeDebugPrivilege is what lets us touch other users' processes and handles.</summary>
    public static bool EnsurePrivileges()
    {
        if (_privilegesEnabled) return true;
        _privilegesEnabled = true;

        var ok = false;
        foreach (var name in new[]
                 {
                     "SeDebugPrivilege", "SeBackupPrivilege",
                     "SeRestorePrivilege", "SeTakeOwnershipPrivilege"
                 })
        {
            if (EnablePrivilege(name)) ok = true;
        }
        return ok;
    }

    private static bool EnablePrivilege(string name)
    {
        if (!Native.Native.OpenProcessToken(Native.Native.GetCurrentProcess(),
                Native.Native.TokenAdjustPrivileges | Native.Native.TokenQuery, out var token))
            return false;

        try
        {
            if (!Native.Native.LookupPrivilegeValueW(null, name, out var luid)) return false;

            var tp = new Native.Native.TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = Native.Native.SePrivilegeEnabled,
            };
            return Native.Native.AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            Native.Native.CloseHandle(token);
        }
    }

    public static KillResult TryKill(int pid)
    {
        EnsurePrivileges();

        var proc = Native.Native.OpenProcess(
            Native.Native.ProcessTerminate |
            Native.Native.ProcessSynchronize |
            Native.Native.ProcessQueryLimitedInformation, false, pid);

        if (proc == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            if (err == 5 && IsProtected(pid))
                return new(KillOutcome.ProtectedProcess,
                    $"PPL-protected (level 0x{ProtectionLevel(pid):X2}); TerminateProcess is refused by the kernel", err);
            return new(KillOutcome.AccessDenied, "OpenProcess(PROCESS_TERMINATE) denied", err);
        }

        try
        {
            if (!Native.Native.TerminateProcess(proc, 1))
            {
                var err = Marshal.GetLastWin32Error();
                return IsProtected(pid)
                    ? new(KillOutcome.ProtectedProcess, "TerminateProcess refused on protected process", err)
                    : new(KillOutcome.AccessDenied, "TerminateProcess failed", err);
            }

            // TerminateProcess only *requests* death. A process blocked in a kernel
            // I/O wait stays in "terminating" state until the driver returns.
            var wait = Native.Native.WaitForSingleObject(proc, 3000);
            if (wait == 0) return new(KillOutcome.Succeeded, "terminated", 0);
            if (wait == Native.Native.WaitFailed)
                return new(KillOutcome.Succeeded, "terminated (could not observe exit)", 0);

            return new(KillOutcome.Zombie,
                "terminate requested but still alive after 3s - blocked in kernel I/O " +
                "(file filter / network / device driver); only a reboot clears it", 0);
        }
        finally
        {
            Native.Native.CloseHandle(proc);
        }
    }
}
