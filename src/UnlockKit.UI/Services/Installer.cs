using System.Diagnostics;

namespace UnlockKit.UI.Services;

/// <summary>Copies the executable to a stable per-user location and handles elevation.</summary>
public static class Installer
{
    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "UnlockKit");

    public static string InstalledExe => Path.Combine(InstallDir, "UnlockKit.exe");

    public static string CurrentExe =>
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName!;

    public static bool IsRunningFromInstall =>
        string.Equals(CurrentExe, InstalledExe, StringComparison.OrdinalIgnoreCase);

    public static string InstallLocally()
    {
        Directory.CreateDirectory(InstallDir);

        if (!IsRunningFromInstall)
            File.Copy(CurrentExe, InstalledExe, overwrite: true);

        DropLegacyInstall();
        return InstalledExe;
    }

    /// <summary>
    /// Removes the pre-1.0 "Unlocker" install folder. Only touched when it
    /// demonstrably holds our old executable, so a user's own directory that
    /// happens to share the name is never deleted.
    /// </summary>
    private static void DropLegacyInstall()
    {
        try
        {
            var legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Unlocker");

            if (!Directory.Exists(legacy)) return;
            if (!File.Exists(Path.Combine(legacy, "Unlocker.exe"))) return;

            Directory.Delete(legacy, recursive: true);
        }
        catch
        {
            // In use by the still-running old build - harmless, retry next time.
        }
    }

    public static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>Restart this same executable as administrator; the caller then exits.</summary>
    public static void RelaunchElevated(string? path)
    {
        var psi = new ProcessStartInfo
        {
            FileName = CurrentExe,
            UseShellExecute = true,
            Verb = "runas",
        };
        if (!string.IsNullOrWhiteSpace(path)) psi.Arguments = $"\"{path}\"";

        Process.Start(psi);
    }
}
