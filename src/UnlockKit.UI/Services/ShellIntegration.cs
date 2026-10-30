using System.Windows;
using Microsoft.Win32;

namespace UnlockKit.UI.Services;

/// <summary>
/// Per-user Explorer context-menu entry. Written to HKCU so no elevation is
/// needed to install or remove it.
///
/// Layout: a single "UnlockKit" parent with a cascading submenu, so Explorer only
/// gains one line instead of two. The children are bare verbs ("Unlock",
/// "Delete") because the parent already names the tool, and each carries its own
/// icon so the two actions stay distinguishable at 16px.
///
/// These are legacy shell verbs: on Windows 11 they live under
/// "Show more options" unless the modern IExplorerCommand is packaged with
/// MSIX app identity. That outer fold is a shell policy, not something a
/// registry verb can opt out of.
/// </summary>
public static class ShellIntegration
{
    /// <summary>Parent key name; also the caption shown in the menu.</summary>
    public const string ParentVerb = "UnlockKit";

    /// <summary>Holds the cascade definition (children live under its "shell" key).</summary>
    private const string MenuDefinitionKey = "UnlockKit.Menu";

    private const string UnlockChild = "Unlock";
    private const string DeleteChild = "Delete";

    /// <summary>Flat verbs written by earlier builds; removed on install/uninstall.</summary>
    private static readonly string[] LegacyVerbs = { "UnlockKit.Open", "UnlockKit.Delete", "Unlocker.Open", "Unlocker.Delete" };

    /// <summary>
    /// Menu keys left behind by the pre-1.0 "Unlocker" naming. Registering the
    /// new name must not leave the old one sitting in the user's context menu.
    /// </summary>
    private static readonly string[] LegacyParents = { "Unlocker" };

    private static readonly string[] LegacyMenuKeys = { "Unlocker.Menu" };

    private static readonly string[] Scopes = { "*", "Directory" };

    public static bool IsInstalled() =>
        Registry.CurrentUser.OpenSubKey($@"Software\Classes\*\shell\{ParentVerb}") is not null;

    public static void Install(string exePath)
    {
        // Child icons must be real files on disk: a menu verb's Icon value
        // accepts "path[,index]", and these live beside the installed exe.
        var unlockIcon = ExtractIcon(exePath, "unlock");
        var deleteIcon = ExtractIcon(exePath, "delete");

        using (var def = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{MenuDefinitionKey}"))
        {
            WriteChild(def, UnlockChild, "Unlock", unlockIcon, $"\"{exePath}\" \"%1\"");
            WriteChild(def, DeleteChild, "Delete", deleteIcon, $"\"{exePath}\" --delete \"%1\"");
            // Only these two children belong in the cascade.
            foreach (var stale in new[] { "Open" })
                def.DeleteSubKeyTree($@"shell\{stale}", throwOnMissingSubKey: false);
        }

        foreach (var scope in Scopes)
        {
            using var parent = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\{scope}\shell\{ParentVerb}");

            parent.SetValue("MUIVerb", ParentVerb);
            parent.SetValue("Icon", exePath);
            parent.SetValue("ExtendedSubCommandsKey", MenuDefinitionKey);

            // SubCommands and a command key would both break the cascade:
            // an explicit SubCommands list overrides ExtendedSubCommandsKey,
            // and a command key turns the parent into a plain verb.
            parent.DeleteValue("SubCommands", throwOnMissingValue: false);
            parent.DeleteSubKeyTree("command", throwOnMissingSubKey: false);
            parent.DeleteSubKeyTree("shell", throwOnMissingSubKey: false);
        }

        foreach (var legacy in LegacyVerbs)
            foreach (var scope in Scopes)
                TryDelete($@"Software\Classes\{scope}\shell\{legacy}");

        foreach (var legacy in LegacyParents)
            foreach (var scope in Scopes)
                TryDelete($@"Software\Classes\{scope}\shell\{legacy}");

        foreach (var legacy in LegacyMenuKeys)
            TryDelete($@"Software\Classes\{legacy}");
    }

    public static void Uninstall()
    {
        foreach (var scope in Scopes)
        {
            TryDelete($@"Software\Classes\{scope}\shell\{ParentVerb}");
            foreach (var legacy in LegacyVerbs) TryDelete($@"Software\Classes\{scope}\shell\{legacy}");
            foreach (var legacy in LegacyParents) TryDelete($@"Software\Classes\{scope}\shell\{legacy}");
        }

        TryDelete($@"Software\Classes\{MenuDefinitionKey}");
        foreach (var legacy in LegacyMenuKeys) TryDelete($@"Software\Classes\{legacy}");
    }

    /// <summary>
    /// Writes an embedded .ico next to the installed exe and returns its path.
    /// Falls back to the exe itself (which carries the app icon) if the
    /// resource is missing.
    /// </summary>
    private static string ExtractIcon(string exePath, string name)
    {
        try
        {
            var dir = Path.Combine(Path.GetDirectoryName(exePath)!, "icons");
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, name + ".ico");

            var res = Application.GetResourceStream(
                new Uri($"pack://application:,,,/Assets/{name}.ico", UriKind.Absolute));

            if (res is null) return exePath;

            using (var src = res.Stream)
            using (var dst = File.Create(target))
                src.CopyTo(dst);

            return target;
        }
        catch
        {
            return exePath;
        }
    }

    private static void WriteChild(RegistryKey parent, string name, string caption, string icon, string command)
    {
        using var child = parent.CreateSubKey($@"shell\{name}");
        // Cascade children are read via MUIVerb; plain verbs use the default
        // value. Writing both keeps the caption identical either way.
        child.SetValue(null, caption);
        child.SetValue("MUIVerb", caption);
        child.SetValue("Icon", icon);

        using var cmd = child.CreateSubKey("command");
        cmd.SetValue(null, command);
    }

    private static void TryDelete(string path)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
        catch
        {
            // Nothing to undo, or the key is in use - not worth failing install.
        }
    }
}
