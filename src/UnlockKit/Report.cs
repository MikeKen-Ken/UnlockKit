using System.Text;
using UnlockKit.Actions;
using UnlockKit.Core;

namespace UnlockKit;

internal sealed class Diagnosis
{
    public required string Path { get; init; }
    public bool Exists { get; init; }
    public bool IsDirectory { get; init; }
    public FileAttributes Attributes { get; init; }
    public required ScanResult Scan { get; init; }
    public bool DeleteAccessGranted { get; set; }
    public int DeleteAccessError { get; set; }
    public required string Verdict { get; set; }
}

internal static class Report
{
    public static Diagnosis Diagnose(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var exists = File.Exists(full) || Directory.Exists(full);

        if (!exists)
        {
            return new Diagnosis
            {
                Path = full, Exists = false, Scan = new ScanResult(),
                Verdict = "path does not exist",
            };
        }

        var attrs = File.GetAttributes(full);
        var isDir = attrs.HasFlag(FileAttributes.Directory);

        ProcessProbe.EnsurePrivileges();
        var scanner = new HandleScanner();
        var scan = scanner.Scan(full);

        // Empirical test: can this account even ask for DELETE on the object?
        var diag = new Diagnosis
        {
            Path = full, Exists = true, IsDirectory = isDir, Attributes = attrs,
            Scan = scan, Verdict = "",
        };

        var handle = Native.Native.CreateFileW(
            full, Native.Native.Delete, Native.Native.FileShareRead | Native.Native.FileShareWrite |
                                        Native.Native.FileShareDelete,
            IntPtr.Zero, Native.Native.OpenExisting,
            Native.Native.FileFlagBackupSemantics | Native.Native.FileFlagOpenReparsePoint, IntPtr.Zero);

        if (handle != IntPtr.Zero && handle != new IntPtr(-1))
        {
            diag.DeleteAccessGranted = true;
            Native.Native.CloseHandle(handle);
        }
        else
        {
            diag.DeleteAccessError = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        }

        diag.Verdict = Classify(diag);
        return diag;
    }

    private static string Classify(Diagnosis d)
    {
        if (d.Scan.SubtreeScan)
        {
            if (d.Scan.Handles.Count > 0)
                return $"LOCKED (subtree): {d.Scan.DistinctPaths.Count()} file(s) inside are held by " +
                       $"{d.Scan.ByProcess.Count()} process(es) - closing them unblocks deleting the folder";
            if (d.DeleteAccessGranted)
                return "NOT LOCKED: nothing inside this folder is held - delete should succeed";
        }

        if (d.Scan.Handles.Count > 0)
        {
            return d.DeleteAccessGranted
                ? "LOCKED but deletable: handles present, POSIX delete will succeed"
                : "LOCKED: user-mode handles opened WITHOUT share-delete - close them first";
        }

        if (d.DeleteAccessGranted)
            return "NOT LOCKED: nothing holds it, delete will succeed";

        return d.DeleteAccessError switch
        {
            5 => "ACL BLOCKS DELETE: no process holds it - this is a permissions problem, not a lock",
            32 => "KERNEL-HELD LOCK: no user-mode handle found - a filter driver (AV/backup/sync) holds it",
            2 => "path not found at open time",
            _ => $"open failed with Win32 error {d.DeleteAccessError}",
        };
    }

    public static void Print(Diagnosis d, bool verbose)
    {
        Line();
        Info("target", d.Path);

        if (!d.Exists)
        {
            Warn(d.Verdict);
            return;
        }

        Info("type", d.IsDirectory ? "directory" : "file");
        Info("attributes", d.Attributes.ToString());
        if (d.Attributes.HasFlag(FileAttributes.ReadOnly))
            Info("readonly", "yes (POSIX delete ignores this flag)");
        Line();

        if (d.Scan.Handles.Count == 0)
        {
            Warn(d.Scan.SubtreeScan
                ? "no user-mode handles on the folder or anything inside it"
                : "no user-mode handles found");
            if (d.Scan.UnreadableHandles > 0)
                Dim($"  ({d.Scan.UnreadableHandles} handles unreadable - run elevated to see them)");
        }
        else
        {
            if (d.Scan.SubtreeScan)
            {
                Info("subtree scan", $"{d.Scan.Handles.Count} handle(s) on files inside this folder, " +
                                     $"held by {d.Scan.ByProcess.Count()} process(es)");
            }
            else
            {
                Info("lockers", $"{d.Scan.Handles.Count} handle(s) across {d.Scan.ByProcess.Count()} process(es)");
            }
            Line();

            foreach (var group in d.Scan.ByProcess)
            {
                var pid = group.Key;
                var protectedFlag = ProcessProbe.IsProtected(pid);
                var tag = protectedFlag ? $"  [PPL 0x{ProcessProbe.ProtectionLevel(pid):X2} - unkillable]" : "";

                Console.WriteLine($"  {ProcessProbe.NameOf(pid),-28} pid {pid}{tag}");

                foreach (var h in group)
                {
                    var access = PathMap.FormatAccess(h.GrantedAccess);
                    Console.WriteLine($"      0x{h.HandleValue:X}  {access}");
                }

                // Subtree scans: name the actual files being held, up to five.
                foreach (var p in group.Select(h => h.MatchedPath)
                                       .Where(p => p is not null)
                                       .Distinct(StringComparer.OrdinalIgnoreCase)
                                       .Take(5))
                    Dim($"      -> {p}");
                var extra = group.Select(h => h.MatchedPath)
                                 .Where(p => p is not null)
                                 .Distinct(StringComparer.OrdinalIgnoreCase).Count() - 5;
                if (extra > 0) Dim($"      ... and {extra} more file(s)");
            }
        }

        Line();
        Info("delete access", d.DeleteAccessGranted
            ? "granted - DELETE right available to this account"
            : $"denied (Win32 {d.DeleteAccessError})");
        Line();
        Console.Write("  verdict: ");
        Console.ForegroundColor = d.Verdict.StartsWith("NOT LOCKED") ? ConsoleColor.Green
            : d.Verdict.StartsWith("LOCKED but") ? ConsoleColor.Yellow
            : ConsoleColor.Red;
        Console.WriteLine(d.Verdict);
        Console.ResetColor();
        Line();
    }

    public static void PrintDelete(DeleteResult r)
    {
        var (label, color) = r.Outcome switch
        {
            DeleteOutcome.DeletedImmediately => ("DELETED", ConsoleColor.Green),
            DeleteOutcome.DeletedViaHandleClose => ("DELETED (after closing handles)", ConsoleColor.Green),
            DeleteOutcome.DeletedTree => ("DELETED (directory tree)", ConsoleColor.Green),
            DeleteOutcome.ScheduledForReboot => ("SCHEDULED FOR REBOOT", ConsoleColor.Yellow),
            _ => ("FAILED", ConsoleColor.Red),
        };

        Console.Write("  result: ");
        Console.ForegroundColor = color;
        Console.Write(label);
        Console.ResetColor();
        Console.WriteLine($"  [{r.Stage}]");
        if (r.Detail.Length > 0) Dim("  " + r.Detail);
    }

    public static string ToJson(Diagnosis d, DeleteResult? result)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        sb.Append($"\"path\":{Quote(d.Path)},");
        sb.Append($"\"exists\":{(d.Exists ? "true" : "false")},");
        sb.Append($"\"deleteAccess\":{(d.DeleteAccessGranted ? "true" : "false")},");
        sb.Append($"\"deleteError\":{d.DeleteAccessError},");
        sb.Append($"\"verdict\":{Quote(d.Verdict)},");
        sb.Append("\"lockers\":[");
        sb.Append(string.Join(",", d.Scan.Handles.GroupBy(h => h.Pid).Select(g =>
            $"{{\"pid\":{g.Key},\"name\":{Quote(ProcessProbe.NameOf(g.Key))}," +
            $"\"protected\":{(ProcessProbe.IsProtected(g.Key) ? "true" : "false")}," +
            $"\"handles\":{g.Count()}," +
            $"\"access\":[{string.Join(",", g.Select(h => h.GrantedAccess))}]}}")));
        sb.Append(']');

        if (result is not null)
        {
            sb.Append($",\"result\":{{\"outcome\":\"{result.Outcome}\",");
            sb.Append($"\"stage\":{Quote(result.Stage)},\"detail\":{Quote(result.Detail)},");
            sb.Append($"\"win32\":{result.Win32Error}}}");
        }

        sb.Append('}');
        return sb.ToString();
    }

    private static string Quote(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static void Line() => Dim("  " + new string('-', 66));

    private static void Info(string key, string value)
    {
        Console.Write($"  {key,-16}");
        Console.WriteLine(value);
    }

    private static void Dim(string s)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(s);
        Console.ResetColor();
    }

    private static void Warn(string s)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  " + s);
        Console.ResetColor();
    }
}
