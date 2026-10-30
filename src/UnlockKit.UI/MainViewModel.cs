using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using UnlockKit.Actions;
using UnlockKit.Core;
using UnlockKit.UI.Models;
using UnlockKit.UI.Services;

namespace UnlockKit.UI;

public sealed class MainViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<LockerRow> Lockers { get; } = new();

    public ICommand ScanCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand RebootCommand { get; }
    public ICommand KillCommand { get; }
    public ICommand MenuCommand { get; }
    public ICommand ElevateCommand { get; }

    private string _targetPath = "";
    private string _verdict = "Pick a file or folder, then click Scan";
    private Brush _verdictBrush = Brushes.Gray;
    private string _status = "Ready";
    private string _logText = "";
    private bool _isBusy;
    private bool _menuInstalled;

    public MainViewModel()
    {
        ScanCommand = new RelayCommand(async () => await ScanAsync(), () => !IsBusy);
        CloseCommand = new RelayCommand(async () => await CloseAsync(), () => !IsBusy && Lockers.Count > 0);
        DeleteCommand = new RelayCommand(async () => await DeleteAsync(), () => !IsBusy);
        RebootCommand = new RelayCommand(async () => await ScheduleRebootAsync(), () => !IsBusy);
        KillCommand = new RelayCommand(async () => await KillAsync(), () => !IsBusy && Lockers.Count > 0);
        MenuCommand = new RelayCommand(() => ToggleMenu());
        ElevateCommand = new RelayCommand(() => Elevate(), () => !Installer.IsElevated());

        _menuInstalled = ShellIntegration.IsInstalled();

        if (!string.IsNullOrWhiteSpace(App.InitialPath))
        {
            TargetPath = App.InitialPath!;
            _ = InitializeAsync();
        }
    }

    /// <summary>Context-menu launch: scan first; for "Delete with UnlockKit" then ask to delete.</summary>
    private async Task InitializeAsync()
    {
        await ScanAsync();
        if (App.AutoDelete) await DeleteAsync();
    }

    public string TargetPath
    {
        get => _targetPath;
        set { _targetPath = value; OnChanged(); }
    }

    public string Verdict
    {
        get => _verdict;
        private set { _verdict = value; OnChanged(); }
    }

    public Brush VerdictBrush
    {
        get => _verdictBrush;
        private set { _verdictBrush = value; OnChanged(); }
    }

    public string Status
    {
        get => _status;
        private set { _status = value; OnChanged(); }
    }

    public string LogText
    {
        get => _logText;
        private set { _logText = value; OnChanged(); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set { _isBusy = value; OnChanged(); RefreshCommands(); }
    }

    public bool MenuInstalled
    {
        get => _menuInstalled;
        private set { _menuInstalled = value; OnChanged(); }
    }

    public string MenuCaption => MenuInstalled ? "Remove context menu" : "Install context menu";
    public string ElevationHint => Installer.IsElevated() ? "Administrator" : "Standard user";

    private async Task ScanAsync()
    {
        var path = ValidatePath();
        if (path is null) return;

        IsBusy = true;
        Status = "Scanning handles…";

        try
        {
            var d = await Task.Run(() => Report.Diagnose(path));
            Apply(d);

            var names = ProcessNames(d);
            Log($"Scan: {d.Scan.Handles.Count} handle(s) across {d.Scan.ByProcess.Count()} process(es)" +
                (names.Length > 0 ? $": {names}" : ""));
            if (d.Scan.SubtreeScan)
                foreach (var p in d.Scan.DistinctPaths.Take(6))
                    Log("  holds: " + p);
        }
        catch (Exception ex)
        {
            Verdict = "Scan failed";
            VerdictBrush = Brushes.OrangeRed;
            Log("Error: " + ex.Message);
        }
        finally
        {
            IsBusy = false;
            Status = "Ready";
        }
    }

    private void Apply(Diagnosis d)
    {
        Lockers.Clear();

        foreach (var g in d.Scan.ByProcess)
        {
            var name = ProcessProbe.NameOf(g.Key);
            var denied = SafetyPolicy.IsDenied(g.Key, name, out var why);
            var access = string.Join(",", g.Select(h => PathMap.FormatAccess(h.GrantedAccess)).Distinct());

            var held = g.Select(h => h.MatchedPath)
                        .Where(p => p is not null)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
            var files = held.Count switch
            {
                0 => "—",
                <= 3 => string.Join("; ", held.Select(System.IO.Path.GetFileName)),
                _ => string.Join("; ", held.Take(3).Select(System.IO.Path.GetFileName)) + $" (+{held.Count - 3} more)",
            };

            Lockers.Add(new LockerRow
            {
                Name = name,
                Pid = g.Key,
                Handles = g.Count(),
                Access = access,
                Files = files,
                Protected = ProcessProbe.IsProtected(g.Key),
                Denied = denied,
                Note = denied ? why : "",
            });
        }

        var names = ProcessNames(d);
        Verdict = names.Length == 0 || d.Verdict.StartsWith("NOT LOCKED")
            ? d.Verdict
            : $"{d.Verdict}   [{names}]";

        VerdictBrush = d.Verdict.StartsWith("NOT LOCKED") ? Brushes.SeaGreen
            : d.Verdict.StartsWith("LOCKED but") ? Brushes.DarkOrange
            : Brushes.Firebrick;

        if (d.Scan.UnreadableHandles > 0)
            Log($"{d.Scan.UnreadableHandles} handle(s) unreadable - restart as administrator to see every process");

        RefreshCommands();
    }

    private static string ProcessNames(Diagnosis d) =>
        string.Join(", ", d.Scan.ByProcess
            .Select(g => ProcessProbe.NameOf(g.Key))
            .Where(n => n.Length > 0)
            .Distinct());

    private async Task CloseAsync()
    {
        var path = ValidatePath();
        if (path is null) return;

        if (!Confirm("Unlock",
                "Closes file handles held by other processes.\n" +
                "The owning program may crash or lose unsaved data.\n\nContinue?"))
            return;

        await RunAsync("Unlock", () =>
        {
            var rep = DeleteAction.CloseHandles(path, new HandleScanner());
            Log($"closed {rep.Closed}, refused {rep.Refused}, unverified {rep.Unverified}, unreachable {rep.Unreachable}");
            foreach (var n in rep.Notes) Log("  " + n);
            if (rep.Error is not null) Log("  " + rep.Error);
        });

        await ScanAsync();
    }

    private async Task DeleteAsync()
    {
        var path = ValidatePath();
        if (path is null) return;

        if (!Confirm("Delete", $"Delete this?\n\n{path}\n\nThis cannot be undone."))
            return;

        DeleteResult? result = null;
        await RunAsync("Delete", () =>
        {
            result = DeleteAction.DeleteAuto(path, new HandleScanner());
            Log($"Result: {result.Outcome} [{result.Stage}] {result.Detail}");
        });

        if (result is { Outcome: DeleteOutcome.Failed })
        {
            var reason = TranslateFailure(result);
            if (Confirm("Delete failed",
                    $"Could not delete immediately.\n\n{reason}\n\nSchedule deletion at next reboot instead?"))
                await ScheduleRebootAsync();
        }
        else
        {
            await ScanAsync();
        }
    }

    private async Task ScheduleRebootAsync()
    {
        var path = ValidatePath();
        if (path is null) return;

        await RunAsync("Schedule reboot-delete", () =>
        {
            var ok = DeleteAction.ScheduleForReboot(path);
            Log(ok ? "Registered in PendingFileRenameOperations - deleted at next boot"
                   : "Scheduling failed - administrator rights required");
        });
    }

    /// <summary>Turn the engine's failure into the actual next step the user needs.</summary>
    private static string TranslateFailure(DeleteResult r)
    {
        if (r.Stage == "tree")
            return "Some items inside the folder could not be deleted.\n" +
                   "Usual cause: a watcher (antivirus, indexer, sync, SVN cache) re-locked a subfolder during deletion.\n" +
                   "Try again; if it keeps failing, use \"End processes\" on the watcher first.";
        if (r.Detail.Contains("image section"))
            return "This is a running program (exe/dll); the kernel holds its image.\n" +
                   "Use \"End processes\" first, then delete.";
        if (r.Detail.Contains("access denied"))
            return "Access denied: your account lacks the DELETE right (an ACL issue, not a lock).\n" +
                   "Take ownership of the file and retry.";
        if (r.Stage == "diagnose")
            return "No closeable user-mode handles found - the lock lives in the kernel.\n" +
                   "If the log mentions unreadable handles, restart as administrator first;\n" +
                   "otherwise a filter driver (AV/backup/sync) holds it and only a reboot-delete works.";
        return "Delete failed: " + r.Detail;
    }

    private async Task KillAsync()
    {
        var path = ValidatePath();
        if (path is null) return;

        if (!Confirm("End processes",
                "Terminates every process holding this path. Unsaved data will be lost.\n\nContinue?"))
            return;

        await RunAsync("End processes", () =>
        {
            var scan = new HandleScanner().Scan(path);
            foreach (var pid in scan.ByProcess.Select(g => g.Key))
            {
                if (SafetyPolicy.IsDenied(pid, ProcessProbe.NameOf(pid), out var why))
                {
                    Log($"skipped {ProcessProbe.NameOf(pid)}: {why}");
                    continue;
                }

                var r = ProcessProbe.TryKill(pid);
                Log($"{ProcessProbe.NameOf(pid)} (pid {pid}) -> {r.Outcome}: {r.Detail}");
            }
        });

        await ScanAsync();
    }

    private async Task RunAsync(string action, Action work)
    {
        IsBusy = true;
        Status = action + "…";

        try
        {
            await Task.Run(work);
        }
        catch (Exception ex)
        {
            Log(action + " failed: " + ex.Message);
        }
        finally
        {
            IsBusy = false;
            Status = "Ready";
        }
    }

    private string? ValidatePath()
    {
        if (string.IsNullOrWhiteSpace(TargetPath))
        {
            Verdict = "Enter a path first";
            VerdictBrush = Brushes.Firebrick;
            return null;
        }

        var full = Path.GetFullPath(TargetPath.Trim().Trim('"'));
        if (!File.Exists(full) && !Directory.Exists(full))
        {
            Verdict = "Path does not exist";
            VerdictBrush = Brushes.Firebrick;
            return null;
        }
        return full;
    }

    private static bool Confirm(string title, string text) =>
        MessageBox.Show(text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private void ToggleMenu()
    {
        try
        {
            if (MenuInstalled)
            {
                ShellIntegration.Uninstall();
                MenuInstalled = false;
                Log("Context menu removed");
            }
            else
            {
                var exe = Installer.InstallLocally();
                ShellIntegration.Install(exe);
                MenuInstalled = true;
                Log($"Installed to {exe} - right-click > UnlockKit > Unlock / Delete " +
                    "(Windows 11 keeps it under \"Show more options\")");
            }
        }
        catch (Exception ex)
        {
            Log("Menu operation failed: " + ex.Message);
        }
    }

    private void Elevate()
    {
        try
        {
            Installer.RelaunchElevated(TargetPath);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Log("Elevation failed: " + ex.Message);
        }
    }

    private void Log(string line)
    {
        LogText = $"[{DateTime.Now:HH:mm:ss}] {line}\n" + LogText;
    }

    private void RefreshCommands()
    {
        foreach (var c in new[] { ScanCommand, CloseCommand, DeleteCommand, RebootCommand, KillCommand, ElevateCommand })
            (c as RelayCommand)?.RaiseCanExecuteChanged();
        OnChanged(nameof(MenuCaption));
        OnChanged(nameof(ElevationHint));
    }

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand : ICommand
{
    private readonly Func<Task>? _asyncExecute;
    private readonly Action? _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _asyncExecute = execute;
        _canExecute = canExecute;
    }

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public async void Execute(object? parameter)
    {
        if (_asyncExecute is not null) await _asyncExecute();
        else _execute?.Invoke();
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
