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

        // Headless menu maintenance, so installers and scripts can wire the
        // shell up without going through the window.
        var installMenu = false;
        var removeMenu = false;

        foreach (var arg in e.Args)
        {
            if (string.Equals(arg, "--delete", StringComparison.OrdinalIgnoreCase))
                AutoDelete = true;
            else if (string.Equals(arg, "--install-menu", StringComparison.OrdinalIgnoreCase))
                installMenu = true;
            else if (string.Equals(arg, "--remove-menu", StringComparison.OrdinalIgnoreCase))
                removeMenu = true;
            else if (!arg.StartsWith('-'))
                InitialPath = arg;
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
        }
    }
}
