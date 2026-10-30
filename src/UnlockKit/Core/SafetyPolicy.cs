namespace UnlockKit.Core;

/// <summary>
/// Hard limits on what this tool is allowed to touch.
///
/// Closing a handle in a process that does not expect it can range from harmless
/// to a bugcheck. The dangerous cases are narrow and known, so they are denied
/// by name rather than judged case by case.
/// </summary>
internal static class SafetyPolicy
{
    /// <summary>
    /// Processes whose handles must never be closed. Keys are base names without
    /// the .exe suffix, compared case-insensitively.
    /// </summary>
    private static readonly Dictionary<string, string> Denied = new(StringComparer.OrdinalIgnoreCase)
    {
        ["System"] = "PID 4 is kernel space; its handles are not ordinary file handles",
        ["Idle"] = "PID 0 is not a real process",
        ["Registry"] = "registry hive process",
        ["smss"] = "session manager; killing it bugchecks the machine",
        ["csrss"] = "client/server runtime; closing its handles can bugcheck the machine",
        ["wininit"] = "session 0 initialiser",
        ["winlogon"] = "logon manager; holds the interactive desktop",
        ["services"] = "service control manager; every service depends on it",
        ["lsass"] = "local security authority",
        ["lsaiso"] = "isolated LSA",
        ["MemCompression"] = "memory compression store",
        ["MsMpEng"] = "antimalware engine; protected and self-healing",
    };

    public static bool IsDenied(int pid, string processName, out string reason)
    {
        reason = "";

        if (pid is 0 or 4)
        {
            reason = Denied[pid == 4 ? "System" : "Idle"];
            return true;
        }

        var baseName = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;

        if (Denied.TryGetValue(baseName, out var why))
        {
            reason = why;
            return true;
        }

        // PPL cannot be touched even with SeDebugPrivilege, so refuse early with a
        // truthful reason instead of letting OpenProcess fail and reporting a
        // generic access error.
        if (ProcessProbe.IsProtected(pid))
        {
            reason = $"PPL-protected (level 0x{ProcessProbe.ProtectionLevel(pid):X2})";
            return true;
        }

        return false;
    }

    /// <summary>Write-capable handles can mean unflushed data; worth telling the user.</summary>
    public static bool HoldsWriteAccess(uint grantedAccess) =>
        (grantedAccess & 0x0002) != 0 || (grantedAccess & 0x0004) != 0;
}
