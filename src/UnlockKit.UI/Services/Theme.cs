using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace UnlockKit.UI.Services;

public enum ThemeKind
{
    /// <summary>Follow whatever Windows is set to (the default on first run).</summary>
    System,
    Light,
    Dark,
}

/// <summary>
/// Runtime light/dark switching.
///
/// The window has no theme-aware code in it: every style reads its colours with
/// DynamicResource, so replacing the single colour dictionary in
/// Application.Resources is enough to repaint everything in place. That is why
/// this class is 100 lines instead of a second set of XAML files.
/// </summary>
public static class Theme
{
    private const string RegPath = @"Software\UnlockKit";
    private const string RegValue = "Theme";

    private static readonly Uri LightUri = new("pack://application:,,,/Themes/Light.xaml", UriKind.Absolute);
    private static readonly Uri DarkUri = new("pack://application:,,,/Themes/Dark.xaml", UriKind.Absolute);

    private static ResourceDictionary? _colourDictionary;
    private static bool _listening;

    /// <summary>What the user picked. <see cref="ThemeKind.System"/> tracks Windows live.</summary>
    public static ThemeKind Preference { get; private set; } = ThemeKind.System;

    /// <summary>What is actually on screen right now.</summary>
    public static bool IsDark { get; private set; }

    public static event EventHandler? Changed;

    public static void Initialize()
    {
        Preference = ReadPreference() ?? ThemeKind.System;
        Apply(Preference, persist: false);

        // Windows can flip its own theme while we are open; only follow it when
        // the user has not made an explicit choice.
        if (_listening) return;
        _listening = true;
        try
        {
            SystemEvents.UserPreferenceChanged += (_, _) =>
            {
                if (Preference != ThemeKind.System) return;
                var dark = SystemPrefersDark();
                if (dark == IsDark) return;

                // This fires on a background thread.
                Application.Current?.Dispatcher.BeginInvoke(() => Apply(ThemeKind.System, false));
            };
        }
        catch
        {
            // No interactive window station; the toggle still works.
        }
    }

    public static void Set(ThemeKind kind, bool persist = true) => Apply(kind, persist);

    public static void Toggle() =>
        Apply(IsDark ? ThemeKind.Light : ThemeKind.Dark, persist: true);

    /// <summary>Look a themed brush up by key, with a neutral fallback.</summary>
    public static Brush Brush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    /// <summary>Re-apply the current theme to a window that was just created.</summary>
    public static void Attach(Window window) => ApplyTitleBar(window, IsDark);

    private static void Apply(ThemeKind kind, bool persist)
    {
        var dark = kind switch
        {
            ThemeKind.Dark => true,
            ThemeKind.Light => false,
            _ => SystemPrefersDark(),
        };

        Preference = kind;
        if (persist) WritePreference(kind);

        if (Application.Current is null) return;

        var loaded = new ResourceDictionary { Source = dark ? DarkUri : LightUri };
        var merged = Application.Current.Resources.MergedDictionaries;

        // Replace the placeholder declared in App.xaml rather than adding a
        // second colour dictionary — merged dictionaries are resolved last-to-
        // first, so a leftover Light.xaml at index 1 would win over a Dark.xaml
        // inserted at index 0.
        _colourDictionary ??= merged.Count > 0 ? merged[0] : null;

        if (_colourDictionary is not null && merged.Contains(_colourDictionary))
            merged[merged.IndexOf(_colourDictionary)] = loaded;
        else
            merged.Insert(0, loaded);

        _colourDictionary = loaded;
        IsDark = dark;

        foreach (Window window in Application.Current.Windows)
            ApplyTitleBar(window, dark);

        Changed?.Invoke(null, EventArgs.Empty);
    }

    // ── system theme ──────────────────────────────────────────────────────

    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }

    private static ThemeKind? ReadPreference()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegPath);
            return (key?.GetValue(RegValue) as string) switch
            {
                "light" => ThemeKind.Light,
                "dark" => ThemeKind.Dark,
                "system" => ThemeKind.System,
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    private static void WritePreference(ThemeKind kind)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegPath);
            key?.SetValue(RegValue, kind switch
            {
                ThemeKind.Light => "light",
                ThemeKind.Dark => "dark",
                _ => "system",
            });
        }
        catch
        {
            // A read-only profile just means the choice does not survive a restart.
        }
    }

    // ── native title bar ──────────────────────────────────────────────────

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// WPF draws its own client area, but the frame and title bar belong to
    /// DWM. Without this the dark window keeps a white title bar — the one
    /// detail that makes a themed app look broken.
    /// </summary>
    private static void ApplyTitleBar(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var value = dark ? 1 : 0;
        if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeLegacy, ref value, sizeof(int));
    }
}
