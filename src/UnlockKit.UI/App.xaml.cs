using System.Windows;
using UnlockKit.UI.Services;

namespace UnlockKit.UI;

public partial class App : Application
{
    /// <summary>Path handed over by the Explorer context menu, if any.</summary>
    public static string? InitialPath { get; private set; }

    /// <summary>Set when the menu entry was "Delete" rather than "Unlock".</summary>
    public static bool AutoDelete { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var args = e.Args;
        var applyIndex = Array.FindIndex(args, a => a.Equals("--apply-update", StringComparison.OrdinalIgnoreCase));

        // This process IS the freshly downloaded build, started by the old one so
        // it can overwrite a file that was still locked. No window, no theme.
        if (applyIndex >= 0)
        {
            Shutdown(ApplyUpdate(args, applyIndex));
            return;
        }

        if (Array.Exists(args, a => a.Equals("--update", StringComparison.OrdinalIgnoreCase)))
        {
            RunHeadlessUpdate();
            return;
        }

        // Before the window exists, so it is painted in the right colours from
        // its first frame instead of flashing white.
        Theme.Initialize();

        // Anything downloaded and not yet applied belongs to an older version;
        // drop it so the folder does not grow a 65 MB binary per release.
        UpdateService.CleanStaging();

        // Headless menu maintenance, so installers and scripts can wire the
        // shell up without going through the window.
        var installMenu = false;
        var removeMenu = false;

        foreach (var arg in args)
        {
            if (string.Equals(arg, "--delete", StringComparison.OrdinalIgnoreCase))
                AutoDelete = true;
            else if (string.Equals(arg, "--install-menu", StringComparison.OrdinalIgnoreCase))
                installMenu = true;
            else if (string.Equals(arg, "--remove-menu", StringComparison.OrdinalIgnoreCase))
                removeMenu = true;
            else if (!arg.StartsWith('-'))
                // Explorer and other shells hand us native separators, but paths
                // pasted from WSL, Node or a URL arrive with forward slashes.
                InitialPath = arg.Replace('/', '\\');
        }

        if (installMenu || removeMenu)
        {
            try
            {
                if (removeMenu) ShellIntegration.Uninstall();
                else ShellIntegration.Install(Installer.InstallLocally());
            }
            catch
            {
                // Never block startup on shell registration; the window has a
                // button that retries and surfaces the error.
            }

            Shutdown(0);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    /// <summary>--apply-update &lt;target-exe&gt; &lt;pid-to-wait-for&gt;</summary>
    private static int ApplyUpdate(string[] args, int flagIndex)
    {
        if (flagIndex + 2 >= args.Length)
        {
            UpdateService.Log("apply: missing target or pid");
            return 2;
        }

        if (!int.TryParse(args[flagIndex + 2], out var pid))
        {
            UpdateService.Log("apply: pid is not a number: " + args[flagIndex + 2]);
            return 2;
        }

        try
        {
            return UpdateService.ApplyFromStaged(args[flagIndex + 1], pid);
        }
        catch (Exception ex)
        {
            UpdateService.Log("apply: " + ex);
            return 1;
        }
    }

    /// <summary>
    /// --update: check, download and install without opening the window, for
    /// scripting and for a scheduled task. Exit code 0 means the new build was
    /// launched (or there was nothing to do), 1 means it failed.
    /// </summary>
    private async void RunHeadlessUpdate()
    {
        try
        {
            var check = await UpdateService.CheckAsync();

            if (check is null || !check.IsNewer)
            {
                UpdateService.Log($"--update: already on {UpdateService.CurrentVersionText}");
                Shutdown(0);
                return;
            }

            var staged = await UpdateService.DownloadAsync(check.Release);
            UpdateService.StartApply(staged, UpdateService.UpdateTarget(), Environment.ProcessId);
            Shutdown(0);
        }
        catch (Exception ex)
        {
            UpdateService.Log("--update failed: " + ex.Message);
            Shutdown(1);
        }
    }
}
