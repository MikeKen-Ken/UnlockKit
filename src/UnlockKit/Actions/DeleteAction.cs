using System.Runtime.InteropServices;
using UnlockKit.Core;
using UnlockKit.Native;

namespace UnlockKit.Actions;

internal enum DeleteOutcome
{
    DeletedImmediately,
    DeletedViaHandleClose,
    DeletedTree,
    ScheduledForReboot,
    Failed
}

internal sealed record DeleteResult(DeleteOutcome Outcome, string Stage, string Detail, int Win32Error = 0)
{
    public bool Success => Outcome != DeleteOutcome.Failed;
}

/// <summary>Outcome of a handle-closing pass, including everything it refused to do.</summary>
internal sealed class CloseReport
{
    public int Closed { get; set; }
    public int Refused { get; set; }
    public int Unverified { get; set; }
    public int Unreachable { get; set; }
    public string? Error { get; set; }
    public List<string> Notes { get; } = new();
}

/// <summary>
/// Deletion ladder. Order matters: the cheapest, least destructive attempt runs
/// first, and each rung explains itself so the caller can report <i>why</i> the
/// fallback was needed instead of just "could not delete".
/// </summary>
internal static class DeleteAction
{
    public static DeleteResult Delete(string path, HandleScanner scanner, bool allowHandleClose, bool allowReboot)
    {
        ProcessProbe.EnsurePrivileges();

        // Rung 1: POSIX-semantics delete. Unlinks the name immediately even while
        // other handles are open - the bytes are freed when the last one closes.
        var posix = TryPosixDelete(path, out var posixStatus, out var posixErr);
        if (posix) return new(DeleteOutcome.DeletedImmediately, "posix", "filename unlinked while handles remain open");

        // Rung 2: a sharing violation means someone holds it without FILE_SHARE_DELETE.
        // Closing those specific handles is the only way POSIX semantics can apply.
        if (allowHandleClose && posixStatus == NtStatus.SharingViolation)
        {
            var closed = CloseHandles(path, scanner);
            if (closed.Closed > 0)
            {
                if (TryPosixDelete(path, out _, out _))
                    return new(DeleteOutcome.DeletedViaHandleClose, "close+posix",
                        $"closed {closed.Closed} blocking handle(s), then unlinked");

                // still stuck: the blocker is not a user-mode handle
                var retry = TryPosixDelete(path, out var s2, out var e2);
                if (retry) return new(DeleteOutcome.DeletedViaHandleClose, "close+posix", "unlinked");
                posixStatus = s2;
                posixErr = e2;
            }
            else
            {
                // Nothing closeable => kernel-held lock (minifilter / mapped image).
                if (!allowReboot)
                    return new(DeleteOutcome.Failed, "diagnose",
                        "sharing violation with no closable user handle - " +
                        "lock is held in kernel (filter driver or image section)", 32);
            }
        }

        if (!allowReboot)
            return new(DeleteOutcome.Failed, "posix", ExplainNtStatus(posixStatus), posixErr);

        // Rung 3: schedule through the session manager at next boot.
        if (Native.Native.MoveFileExW(path, null, Native.Native.MoveFileDelayUntilReboot))
            return new(DeleteOutcome.ScheduledForReboot, "reboot",
                "registered in PendingFileRenameOperations", 0);

        return new(DeleteOutcome.Failed, "reboot",
            ExplainNtStatus(posixStatus), Marshal.GetLastWin32Error());
    }

    /// <summary>
    /// Dispatch: files go through the ladder, directories go through the
    /// recursive tree delete. Watchers (TSVNCache, indexers, sync clients)
    /// re-open directory handles milliseconds after losing them, so a tree
    /// delete must close the whole subtree and then delete bottom-up faster
    /// than the watcher can relock.
    /// </summary>
    public static DeleteResult DeleteAuto(string path, HandleScanner scanner)
        => Directory.Exists(path) ? DeleteTree(path, scanner)
                                  : Delete(path, scanner, allowHandleClose: true, allowReboot: false);

    public static DeleteResult DeleteTree(string root, HandleScanner scanner)
    {
        ProcessProbe.EnsurePrivileges();

        // Pass 1: close every handle inside the subtree in a single scan.
        var closed = CloseHandles(root, scanner);

        // Pass 2: recursive POSIX delete, files first, directories post-order.
        var failures = new List<string>();
        DeleteRecursive(root, scanner, failures, depth: 0);

        if (failures.Count == 0)
            return new(DeleteOutcome.DeletedTree, "tree",
                $"deleted directory tree (closed {closed.Closed} handle(s) in {closed.Refused} refused / " +
                $"{closed.Unverified} unverified / {closed.Unreachable} unreachable first)", 0);

        return new(DeleteOutcome.Failed, "tree",
            $"{failures.Count} item(s) survived; first failure: {failures[0]}", 32);
    }

    private static void DeleteRecursive(string dir, HandleScanner scanner, List<string> failures, int depth)
    {
        if (depth > 64) { failures.Add(dir + " : nesting too deep, refusing"); return; }

        foreach (var file in SafeEnumerate(() => Directory.EnumerateFiles(dir)))
        {
            if (TryPosixDelete(file, out _, out _)) continue;
            // a handle appeared after pass 1; close just this file and retry once
            CloseHandles(file, scanner);
            if (TryPosixDelete(file, out _, out _)) continue;
            failures.Add(file);
        }

        foreach (var sub in SafeEnumerate(() => Directory.EnumerateDirectories(dir)))
        {
            // Never recurse into a junction/symlink - delete the link itself.
            bool isLink;
            try { isLink = File.GetAttributes(sub).HasFlag(FileAttributes.ReparsePoint); }
            catch { isLink = false; }

            if (isLink)
            {
                if (!TryPosixDelete(sub, out _, out _)) failures.Add(sub);
                continue;
            }

            DeleteRecursive(sub, scanner, failures, depth + 1);
        }

        // The directory itself, now empty. Watchers re-open their handles within
        // milliseconds, so each retry closes whatever re-appeared and backs off.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (TryPosixDelete(dir, out _, out _)) return;
            CloseHandles(dir, scanner);
            Thread.Sleep(60 * (attempt + 1));
        }
        if (TryPosixDelete(dir, out _, out _)) return;
        failures.Add(dir);
    }

    private static List<string> SafeEnumerate(Func<IEnumerable<string>> enumerate)
    {
        try { return enumerate().ToList(); }
        catch { return new List<string>(); }
    }

    public static bool TryPosixDelete(string path, out uint ntStatus, out int win32)
    {
        ntStatus = 0;
        win32 = 0;

        var handle = Native.Native.CreateFileW(
            path,
            Native.Native.Delete | Native.Native.Synchronize,
            Native.Native.FileShareRead | Native.Native.FileShareWrite | Native.Native.FileShareDelete,
            IntPtr.Zero,
            Native.Native.OpenExisting,
            Native.Native.FileFlagBackupSemantics | Native.Native.FileFlagOpenReparsePoint,
            IntPtr.Zero);

        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            win32 = Marshal.GetLastWin32Error();
            ntStatus = win32 switch
            {
                32 => NtStatus.SharingViolation,
                5 => NtStatus.AccessDenied,
                _ => 0,
            };
            return false;
        }

        try
        {
            var info = new FileDispositionInformationEx
            {
                Flags = Native.Native.FileDispositionFlagDelete
                      | Native.Native.FileDispositionFlagPosixSemantics
                      | Native.Native.FileDispositionFlagIgnoreReadonlyAttribute,
            };

            var status = Native.Native.NtSetInformationFile(
                handle, out _, ref info, Marshal.SizeOf<FileDispositionInformationEx>(),
                Native.Native.FileDispositionInformationExClass);

            ntStatus = status;
            if (status != NtStatus.Success) win32 = 5;
            return status == NtStatus.Success;
        }
        finally
        {
            Native.Native.CloseHandle(handle);
        }
    }

    public static CloseReport CloseHandles(string path, HandleScanner scanner)
    {
        ProcessProbe.EnsurePrivileges();

        var report = new CloseReport();

        // Same predicate the scan used (exact for files, subtree for directories):
        // re-checked against every handle immediately before closing it.
        Func<IntPtr, bool> verify;
        try
        {
            verify = scanner.CreateVerifier(path);
        }
        catch (InvalidOperationException e)
        {
            report.Error = e.Message;
            return report;
        }

        var scan = scanner.Scan(path);
        var self = Native.Native.GetCurrentProcess();

        foreach (var group in scan.ByProcess)
        {
            var name = ProcessProbe.NameOf(group.Key);

            if (SafetyPolicy.IsDenied(group.Key, name, out var why))
            {
                report.Refused += group.Count();
                report.Notes.Add($"refused {name} (pid {group.Key}): {why}");
                continue;
            }

            var proc = Native.Native.OpenProcess(Native.Native.ProcessDupHandle, false, group.Key);
            if (proc == IntPtr.Zero)
            {
                report.Unreachable += group.Count();
                report.Notes.Add($"cannot open {name} (pid {group.Key}); run elevated");
                continue;
            }

            try
            {
                foreach (var h in group)
                {
                    var remote = new IntPtr(h.HandleValue);

                    // Step 1: take a copy WITHOUT closing the source, and re-check
                    // identity. Between enumeration and now the source handle may
                    // have been closed and its value recycled onto an unrelated
                    // object; closing blind would hit the wrong thing.
                    if (!Native.Native.DuplicateHandle(
                            proc, remote, self, out var probe, 0, false, Native.Native.DuplicateSameAccess))
                    {
                        report.Unverified++;
                        continue;
                    }

                    bool stillTarget;
                    try
                    {
                        stillTarget = verify(probe);
                    }
                    finally
                    {
                        Native.Native.CloseHandle(probe);
                    }

                    if (!stillTarget)
                    {
                        report.Unverified++;
                        report.Notes.Add($"skipped a recycled handle in {name} (pid {group.Key})");
                        continue;
                    }

                    if (SafetyPolicy.HoldsWriteAccess(h.GrantedAccess))
                        report.Notes.Add(
                            $"{name} (pid {group.Key}) holds WRITE access; unsaved data may be lost");

                    // Step 2: only now close it in the owning process. The window
                    // between the check above and this call is microseconds, but it
                    // is not zero - there is no API to close a remote handle
                    // conditionally on the object it points at.
                    if (Native.Native.DuplicateHandle(
                            proc, remote, self, out var closed, 0, false, Native.Native.DuplicateCloseSource))
                    {
                        Native.Native.CloseHandle(closed);
                        report.Closed++;
                    }
                    else
                    {
                        report.Unverified++;
                    }
                }
            }
            finally
            {
                Native.Native.CloseHandle(proc);
            }
        }

        return report;
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

    /// <summary>Register the path in PendingFileRenameOperations for the next boot.</summary>
    public static bool ScheduleForReboot(string path)
    {
        ProcessProbe.EnsurePrivileges();
        return Native.Native.MoveFileExW(path, null, Native.Native.MoveFileDelayUntilReboot);
    }

    private static string ExplainNtStatus(uint status) => status switch
    {
        NtStatus.SharingViolation => "sharing violation: a handle opened without FILE_SHARE_DELETE",
        NtStatus.AccessDenied => "access denied: ACL lacks DELETE right for this account",
        NtStatus.CannotDelete => "cannot delete: file is mapped as an image section (running exe/dll)",
        0 => "unknown failure",
        _ => $"NTSTATUS 0x{status:X8}",
    };
}
