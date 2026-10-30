using UnlockKit;
using UnlockKit.Actions;
using UnlockKit.Core;

var args_ = args.ToList();
if (args_.Count == 0 || args_.Contains("-h") || args_.Contains("--help"))
{
    Console.WriteLine("""
    unlockkit-cli - find out why a file won't delete, then fix it

    usage:
      unlockkit-cli <path>                    diagnose only (default)
      unlockkit-cli diagnose <path>           same, explicit
      unlockkit-cli delete <path>             delete now (POSIX semantics), no reboot fallback
      unlockkit-cli delete <path> --reboot    allow scheduling at next boot if needed
      unlockkit-cli close <path>              close the blocking handles, keep the file
      unlockkit-cli kill <path>               terminate the processes holding it

    flags:
      --json       machine-readable output
      -v           verbose (show full object paths per handle)
      -h, --help   this text

    exit codes:
      0  success / not locked
      1  locked, action refused or failed
      2  usage or internal error
    """);
    return 2;
}

var verb = "diagnose";
if (args_.Count > 1 && !args_[0].StartsWith('-') &&
    args_[0] is "diagnose" or "delete" or "close" or "kill")
{
    verb = args_[0];
    args_.RemoveAt(0);
}

var flags = args_.Where(a => a.StartsWith('-')).ToHashSet(StringComparer.OrdinalIgnoreCase);
var pathArgs = args_.Where(a => !a.StartsWith('-')).ToList();

if (pathArgs.Count != 1)
{
    Console.Error.WriteLine("error: exactly one path required");
    return 2;
}

var target = pathArgs[0];
var json = flags.Contains("--json");
var verbose = flags.Contains("-v") || flags.Contains("--verbose");
var allowReboot = flags.Contains("--reboot");

try
{
    switch (verb)
    {
        case "diagnose":
        {
            var d = Report.Diagnose(target);
            if (json) Console.WriteLine(Report.ToJson(d, null));
            else Report.Print(d, verbose);
            return d.Scan.Handles.Count == 0 ? 0 : 1;
        }

        case "delete":
        {
            var d = Report.Diagnose(target);
            var scanner = new HandleScanner();
            var r = DeleteAction.DeleteAuto(System.IO.Path.GetFullPath(target), scanner);

            if (json) Console.WriteLine(Report.ToJson(d, r));
            else
            {
                Report.Print(d, false);
                Report.PrintDelete(r);
            }
            return r.Success ? 0 : 1;
        }

        case "close":
        {
            var d = Report.Diagnose(target);
            var rep = DeleteAction.CloseHandles(System.IO.Path.GetFullPath(target), new HandleScanner());

            if (json)
            {
                Console.WriteLine(
                    $"{{\"closed\":{rep.Closed},\"refused\":{rep.Refused}," +
                    $"\"unverified\":{rep.Unverified},\"unreachable\":{rep.Unreachable}}}");
            }
            else
            {
                Report.Print(d, false);
                Console.WriteLine($"  closed {rep.Closed}  refused {rep.Refused}  " +
                                  $"unverified {rep.Unverified}  unreachable {rep.Unreachable}");
                foreach (var note in rep.Notes) Console.WriteLine("    - " + note);

                if (rep.Closed == 0 && rep.Refused == 0)
                    Console.WriteLine("  nothing closeable - the lock is held in kernel, not by a user handle");
            }
            return rep.Closed > 0 ? 0 : 1;
        }

        case "kill":
        {
            var full = System.IO.Path.GetFullPath(target);
            var scan = new HandleScanner().Scan(full);
            var pids = scan.ByProcess.Select(g => g.Key).ToList();

            if (pids.Count == 0)
            {
                Console.WriteLine("  nothing holds this path - nothing to kill");
                return 0;
            }

            var anyFailure = false;
            foreach (var pid in pids)
            {
                var r = ProcessProbe.TryKill(pid);
                var color = r.Outcome == KillOutcome.Succeeded ? ConsoleColor.Green : ConsoleColor.Red;
                Console.ForegroundColor = color;
                Console.Write($"  {ProcessProbe.NameOf(pid),-28} pid {pid,-6} {r.Outcome}");
                Console.ResetColor();
                Console.WriteLine($"  {r.Detail}");
                if (r.Outcome != KillOutcome.Succeeded) anyFailure = true;
            }
            return anyFailure ? 1 : 0;
        }

        default:
            Console.Error.WriteLine($"error: unknown verb '{verb}'");
            return 2;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.GetType().Name}: {ex.Message}");
    return 2;
}
