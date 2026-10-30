using System.Runtime.InteropServices;
using UnlockKit.Native;

namespace UnlockKit.Core;

internal sealed class FileHandle
{
    public int Pid { get; init; }
    public string ProcessName { get; init; } = "";
    public long HandleValue { get; init; }
    public uint GrantedAccess { get; init; }
    /// <summary>Resolved path of the matched handle (subtree mode only).</summary>
    public string? MatchedPath { get; init; }
    public bool AllowsDelete => (GrantedAccess & Native.Native.Delete) != 0;
}

internal sealed class ScanResult
{
    public List<FileHandle> Handles { get; } = new();
    public int UnreadableHandles { get; set; }
    public int FileHandlesExamined { get; set; }
    public bool IdentityKnown { get; set; }
    /// <summary>True when the target was a directory and the whole subtree was matched.</summary>
    public bool SubtreeScan { get; set; }

    public IEnumerable<string> DistinctPaths =>
        Handles.Where(h => h.MatchedPath is not null)
               .Select(h => h.MatchedPath!)
               .Distinct(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<IGrouping<int, FileHandle>> ByProcess =>
        Handles.GroupBy(h => h.Pid).OrderByDescending(g => g.Count());
}

/// <summary>
/// Finds every handle pointing at the target file - or, when the target is a
/// directory, at anything inside it. Deleting a folder means deleting its
/// children first, so a lock on any file in the subtree blocks the folder even
/// when nothing holds the folder object itself.
///
/// Matching is done by object identity - volume serial number plus 64-bit file
/// index - not by resolving object names. NtQueryObject on thousands of file
/// handles deadlocks on some filter drivers; GetFileInformationByHandle cannot,
/// and identity is exact even when the file was opened via a hardlink or a
/// different path spelling. Subtree matching additionally resolves paths, but
/// only for handles already proven to sit on the target's volume, so network
/// redirector handles are never queried.
/// </summary>
internal sealed class HandleScanner
{
    private static int _fileTypeIndex = -1;

    public ScanResult Scan(string fullPath)
    {
        var result = new ScanResult();

        if (!TryGetIdentity(fullPath, out var volume, out var fileId))
        {
            // Cannot open the target at all (0 access, all share bits). Without an
            // identity there is nothing to match against.
            result.IdentityKnown = false;
            return result;
        }

        result.IdentityKnown = true;

        bool isDirectory;
        try { isDirectory = File.GetAttributes(fullPath).HasFlag(FileAttributes.Directory); }
        catch { isDirectory = false; }
        result.SubtreeScan = isDirectory;

        var rootUpper = isDirectory
            ? Path.GetFullPath(fullPath).TrimEnd('\\').ToUpperInvariant() + "\\"
            : "";

        Debug($"target volume=0x{volume:X8} fileId=0x{fileId:X} subtree={isDirectory}");

        if (_fileTypeIndex < 0) _fileTypeIndex = DiscoverFileTypeIndex();
        Debug($"typeIndex={_fileTypeIndex}");

        var entries = QueryAllHandles();
        Debug($"totalHandles={entries.Count}");
        var processHandles = new Dictionary<int, IntPtr>();
        var self = Native.Native.GetCurrentProcess();

        if (_fileTypeIndex < 0)
        {
            result.UnreadableHandles = -1; // signals "type index unknown" to the caller
            return result;
        }

        try
        {
            foreach (var entry in entries)
            {
                if (_fileTypeIndex >= 0 && entry.ObjectTypeIndex != _fileTypeIndex) continue;

                var pid = (int)entry.ProcessId.ToUInt64();
                if (pid <= 0) continue;

                if (!processHandles.TryGetValue(pid, out var proc))
                {
                    proc = Native.Native.OpenProcess(
                        Native.Native.ProcessDupHandle | Native.Native.ProcessQueryLimitedInformation,
                        false, pid);
                    processHandles[pid] = proc;
                }

                if (proc == IntPtr.Zero)
                {
                    result.UnreadableHandles++;
                    continue;
                }

                var remote = new IntPtr((long)entry.HandleValue.ToUInt64());
                if (!Native.Native.DuplicateHandle(proc, remote, self, out var local, 0, false,
                        Native.Native.DuplicateSameAccess))
                {
                    result.UnreadableHandles++;
                    continue;
                }

                try
                {
                    // The File object type also covers pipes and character devices.
                    // GetFileInformationByHandle blocks forever on a synchronous pipe,
                    // so reject anything that is not a disk file before touching it.
                    if (Native.Native.GetFileType(local) != 1) continue; // FILE_TYPE_DISK

                    result.FileHandlesExamined++;

                    if (!TryGetIdentity(local, out var v, out _)) continue;
                    if (v != volume) continue; // different volume: no exact or subtree match possible

                    string? matchedPath = null;
                    if (!MatchesTarget(local, volume, fileId, rootUpper, out matchedPath)) continue;

                    result.Handles.Add(new FileHandle
                    {
                        Pid = pid,
                        ProcessName = ProcessProbe.NameOf(pid),
                        HandleValue = remote.ToInt64(),
                        GrantedAccess = entry.GrantedAccess,
                        MatchedPath = matchedPath,
                    });
                }
                finally
                {
                    Native.Native.CloseHandle(local);
                }
            }
        }
        finally
        {
            foreach (var h in processHandles.Values)
                if (h != IntPtr.Zero) Native.Native.CloseHandle(h);
        }

        Debug($"done examined={result.FileHandlesExamined} matches={result.Handles.Count} " +
              $"unreadable={result.UnreadableHandles}");
        return result;
    }

    /// <summary>
    /// Builds the same predicate <see cref="Scan"/> uses, for verifying a handle
    /// immediately before closing it. Exact mode: identity equals the target.
    /// Subtree mode: identity equals the target, or the resolved path falls
    /// under the target directory.
    /// </summary>
    public Func<IntPtr, bool> CreateVerifier(string fullPath)
    {
        if (!TryGetIdentity(fullPath, out var volume, out var fileId))
            throw new InvalidOperationException("cannot open target; no identity to verify against");

        bool isDirectory;
        try { isDirectory = File.GetAttributes(fullPath).HasFlag(FileAttributes.Directory); }
        catch { isDirectory = false; }

        var rootUpper = isDirectory
            ? Path.GetFullPath(fullPath).TrimEnd('\\').ToUpperInvariant() + "\\"
            : "";

        return handle => MatchesTarget(handle, volume, fileId, rootUpper, out _);
    }

    private static bool MatchesTarget(
        IntPtr localHandle, uint volume, ulong fileId, string rootUpper, out string? matchedPath)
    {
        matchedPath = null;

        if (!TryGetIdentity(localHandle, out var v, out var id)) return false;
        if (v != volume) return false;
        if (id == fileId) return true; // the target itself
        if (rootUpper.Length == 0) return false; // exact mode, not the target

        // Subtree mode: resolve the path and test containment. Only same-volume
        // handles reach this point, so network paths are never queried.
        if (!TryGetPath(localHandle, out var path)) return false;
        if (!path.ToUpperInvariant().StartsWith(rootUpper, StringComparison.Ordinal)) return false;

        matchedPath = path;
        return true;
    }

    internal static bool TryGetPath(IntPtr handle, out string path)
    {
        path = "";
        var buf = new char[1024];
        var n = Native.Native.GetFinalPathNameByHandleW(handle, buf, buf.Length, 0);
        if (n <= 0) return false;
        if (n >= buf.Length)
        {
            buf = new char[n + 2];
            n = Native.Native.GetFinalPathNameByHandleW(handle, buf, buf.Length, 0);
            if (n <= 0 || n >= buf.Length) return false;
        }
        path = new string(buf, 0, n);
        if (path.StartsWith(@"\\?\")) path = path[4..];
        return true;
    }

    private static void Debug(string message)
    {
        if (Environment.GetEnvironmentVariable("UNLOCKKIT_DEBUG") == "1")
            Console.Error.WriteLine("[debug] " + message);
    }

    internal static bool TryGetIdentity(string path, out uint volume, out ulong index)
    {
        volume = 0;
        index = 0;

        var handle = Native.Native.CreateFileW(
            path, 0,
            Native.Native.FileShareRead | Native.Native.FileShareWrite | Native.Native.FileShareDelete,
            IntPtr.Zero, Native.Native.OpenExisting,
            Native.Native.FileFlagBackupSemantics | Native.Native.FileFlagOpenReparsePoint,
            IntPtr.Zero);

        if (handle == new IntPtr(-1)) return false;

        try
        {
            return TryGetIdentity(handle, out volume, out index);
        }
        finally
        {
            if (handle != IntPtr.Zero) Native.Native.CloseHandle(handle);
        }
    }

    private static bool TryGetIdentity(IntPtr handle, out uint volume, out ulong index)
    {
        volume = 0;
        index = 0;

        if (!Native.Native.GetFileInformationByHandle(handle, out var info)) return false;

        volume = info.VolumeSerialNumber;
        index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        return true;
    }

    private static int DiscoverFileTypeIndex()
    {
        var temp = Path.Combine(Path.GetTempPath(), "unlockkit-probe-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(temp, "x");

        var handle = Native.Native.CreateFileW(
            temp, 0x00010000, Native.Native.FileShareRead | Native.Native.FileShareWrite | Native.Native.FileShareDelete,
            IntPtr.Zero, Native.Native.OpenExisting, 0, IntPtr.Zero);

        try
        {
            if (handle == new IntPtr(-1)) return -1;

            var mine = (ulong)handle.ToInt64();
            var pid = (ulong)Environment.ProcessId;

            foreach (var e in QueryAllHandles())
            {
                if (e.ProcessId.ToUInt64() == pid && e.HandleValue.ToUInt64() == mine)
                    return e.ObjectTypeIndex;
            }
            return -1;
        }
        finally
        {
            if (handle != IntPtr.Zero && handle != new IntPtr(-1)) Native.Native.CloseHandle(handle);
            try { File.Delete(temp); } catch { /* best effort */ }
        }
    }

    private static List<SystemHandleEntry> QueryAllHandles()
    {
        var size = 1 << 21;
        var entrySize = Marshal.SizeOf<SystemHandleEntry>();

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = Native.Native.NtQuerySystemInformation(
                    Native.Native.SystemExtendedHandleInformation, buffer, size, out var needed);

                if (status == NtStatus.InfoLengthMismatch ||
                    status == NtStatus.BufferTooSmall ||
                    status == NtStatus.BufferOverflow)
                {
                    size = Math.Max(needed + (1 << 16), size * 2);
                    continue;
                }

                if (status != NtStatus.Success)
                    throw new InvalidOperationException($"NtQuerySystemInformation failed: 0x{status:X8}");

                var count = (long)(ulong)Marshal.ReadIntPtr(buffer);
                var list = new List<SystemHandleEntry>((int)Math.Min(count, 1 << 20));
                var cursor = buffer + IntPtr.Size * 2;

                for (long i = 0; i < count; i++)
                {
                    list.Add(Marshal.PtrToStructure<SystemHandleEntry>(cursor));
                    cursor += entrySize;
                }
                return list;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        throw new InvalidOperationException("handle table kept growing; giving up");
    }
}
