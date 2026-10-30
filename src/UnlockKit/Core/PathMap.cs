using System.Text;

namespace UnlockKit.Core;

/// <summary>
/// Translates between Win32 dos paths ("C:\Windows") and NT device paths
/// ("\Device\HarddiskVolume3\Windows"). Handle names always come back in the
/// NT form, so every comparison against a user-supplied path needs this.
/// </summary>
internal static class PathMap
{
    private static List<KeyValuePair<string, string>>? _map;

    private static List<KeyValuePair<string, string>> Map
    {
        get
        {
            if (_map is not null) return _map;

            var list = new List<KeyValuePair<string, string>>();
            var buffer = new char[1024];

            foreach (var drive in DriveInfo.GetDrives())
            {
                var letter = drive.Name.TrimEnd('\\'); // "C:"
                if (letter.Length != 2) continue;
                var len = Native.Native.QueryDosDeviceW(letter, buffer, buffer.Length);
                if (len <= 0) continue;
                var target = new string(buffer, 0, len).TrimEnd('\0');
                if (target.Length == 0) continue;
                list.Add(new(target, letter));
            }

            // longest device path first so \Device\HarddiskVolume10 wins over ...Volume1
            list.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
            _map = list;
            return list;
        }
    }

    public static string ToDevicePath(string dosPath)
    {
        if (dosPath.StartsWith(@"\\?\", StringComparison.Ordinal))
            dosPath = dosPath[4..];

        if (dosPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // UNC: \\server\share => \Device\Mup\server\share
            return @"\Device\Mup" + dosPath[1..];
        }

        if (dosPath.Length >= 2 && dosPath[1] == ':')
        {
            var drive = dosPath[..2].ToUpperInvariant();
            foreach (var kv in Map)
                if (string.Equals(kv.Key, drive, StringComparison.OrdinalIgnoreCase))
                    return kv.Value + dosPath[2..];
        }

        return dosPath;
    }

    /// <summary>Best-effort device path -> friendly path, for display only.</summary>
    public static string ToDosPath(string devicePath)
    {
        foreach (var kv in Map)
        {
            if (devicePath.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
                return kv.Value + devicePath[kv.Key.Length..];
        }
        return devicePath;
    }

    /// <summary>True when the handle name refers to the target itself or something beneath it.</summary>
    public static bool RefersTo(string handleDevicePath, string targetDevicePath)
    {
        if (handleDevicePath.Length == 0) return false;

        var a = handleDevicePath.TrimEnd('\\');
        var b = targetDevicePath.TrimEnd('\\');

        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;

        // handle opened on a parent directory of the target, or a child of it
        if (b.Length > a.Length && b.StartsWith(a + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        if (a.Length > b.Length && a.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    public static string Quote(string s) => "\"" + s + "\"";

    public static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..Math.Max(0, max - 3)] + "...";

    public static string FormatAccess(uint access)
    {
        var sb = new StringBuilder();
        void Add(uint bit, string name)
        {
            if ((access & bit) == 0) return;
            if (sb.Length > 0) sb.Append('|');
            sb.Append(name);
        }

        Add(0x0001, "READ");
        Add(0x0002, "WRITE");
        Add(0x0004, "APPEND");
        Add(0x0020, "EXECUTE");
        Add(Native.Native.Delete, "DELETE");
        Add(0x00040000, "WRITEDAC");
        Add(0x00080000, "WRITEOWNER");
        Add(0x00100000, "SYNC");

        return sb.Length == 0 ? $"0x{access:X8}" : sb.ToString();
    }
}
