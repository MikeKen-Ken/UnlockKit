using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace UnlockKit.UI.Services;

/// <summary>A published build, as described by the GitHub release metadata.</summary>
public sealed record ReleaseInfo(
    Version Version,
    string Tag,
    string PageUrl,
    string Notes,
    string AssetName,
    string AssetUrl,
    long AssetSize,
    string? Sha256Url)
{
    /// <summary>1.2.0.0 -> "1.2.0".</summary>
    public string Display => Version.ToString(3);
}

public sealed record CheckResult(ReleaseInfo Release, bool IsNewer);

/// <summary>
/// Self-update against this project's GitHub releases.
///
/// Deliberately not an installer framework: the release carries one
/// self-contained exe, so an update is "download that exe, then let it replace
/// the running one". The replacement is done by the *new* build launching
/// itself with <c>--apply-update</c>, which avoids a .bat/.ps1 shim and the
/// codepage problems those bring on a non-English Windows.
/// </summary>
public static class UpdateService
{
    private const string Owner = "MikeKen-Ken";
    private const string Repo = "UnlockKit";
    private const string RegPath = @"Software\UnlockKit";

    // GitHub rejects requests without a User-Agent.
    private static readonly Lazy<HttpClient> ApiClient = new(() =>
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("UnlockKit", CurrentVersionText));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    });

    // The asset is ~65 MB; HttpClient.Timeout would abort a slow but healthy
    // download, so the transfer client has no fixed cap of its own.
    private static readonly Lazy<HttpClient> TransferClient =
        new(() => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });

    public static Version CurrentVersion { get; } =
        Normalize(typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0, 0));

    public static string CurrentVersionText => CurrentVersion.ToString(3);

    /// <summary>Set UNLOCKKIT_NO_UPDATE=1 to stop the automatic check on launch.</summary>
    public static bool IsDisabled =>
        Environment.GetEnvironmentVariable("UNLOCKKIT_NO_UPDATE") is "1" or "true" or "TRUE";

    public static string StagingDir => Path.Combine(BaseDir, "updates");

    public static string LogPath => Path.Combine(BaseDir, "update.log");

    private static string BaseDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnlockKit");

    // ── querying ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Asks GitHub for the newest stable release. Returns null when the project
    /// has no releases yet; throws on network or protocol failure so the caller
    /// can decide whether that is worth reporting.
    /// </summary>
    public static async Task<CheckResult?> CheckAsync(CancellationToken ct = default)
    {
        var release = await FetchLatestAsync(ct).ConfigureAwait(false);
        if (release is null) return null;

        var newer = release.Version > CurrentVersion;
        Log($"check: running {CurrentVersionText}, latest {release.Display}" + (newer ? " (newer)" : ""));
        return new CheckResult(release, newer);
    }

    // Escape hatch for tests and private mirrors: any URL that returns the same
    // JSON shape as the GitHub releases API. Unset in normal use.
    //   set UNLOCKKIT_UPDATE_FEED=http://127.0.0.1:8099/latest.json
    private static async Task<ReleaseInfo?> FetchLatestAsync(CancellationToken ct)
    {
        var url = Environment.GetEnvironmentVariable("UNLOCKKIT_UPDATE_FEED")
                  ?? $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

        using var response = await ApiClient.Value
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        // No published release at all: not an error, just nothing to offer.
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;

        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;

        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        var version = ParseVersion(tag);
        if (version is null) return null;

        var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
        var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

        string? assetName = null, assetUrl = null, shaUrl = null;
        long assetSize = 0;

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            var listed = assets.EnumerateArray().ToList();

            foreach (var asset in listed)
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";

                // The GUI binary only; the CLI ships as a zip and is not what gets
                // swapped into %LOCALAPPDATA%.
                if (!name.StartsWith("UnlockKit", StringComparison.OrdinalIgnoreCase)) continue;
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Contains("cli", StringComparison.OrdinalIgnoreCase)) continue;

                assetName = name;
                assetUrl = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                assetSize = asset.TryGetProperty("size", out var size) ? size.GetInt64() : 0;
                break;
            }

            // Assets can be listed in any order, so the checksum is a second pass.
            if (assetName is not null)
            {
                foreach (var asset in listed)
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (!name.Equals(assetName + ".sha256", StringComparison.OrdinalIgnoreCase)) continue;

                    shaUrl = asset.TryGetProperty("browser_download_url", out var s2) ? s2.GetString() : null;
                    break;
                }
            }
        }

        if (assetUrl is null) return null;

        return new ReleaseInfo(version, tag, page, notes, assetName!, assetUrl, assetSize, shaUrl);
    }

    // ── downloading ──────────────────────────────────────────────────────────

    /// <summary>
    /// Downloads the release binary into the staging folder and verifies it.
    /// Returns the staged path. A previously staged file for the same version is
    /// reused, so relaunching the app does not pull 65 MB again.
    /// </summary>
    public static async Task<string> DownloadAsync(
        ReleaseInfo release, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(StagingDir);
        var path = Path.Combine(StagingDir, $"UnlockKit-{release.Display}.exe");

        if (File.Exists(path) && MatchesVersion(path, release.Version))
        {
            Log($"download: reusing staged {path}");
            progress?.Report(1);
            return path;
        }

        var partial = path + ".part";
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        try
        {
            using var response = await TransferClient.Value
                .GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? release.AssetSize;

            await using (var source = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false))
            await using (var target = File.Create(partial))
            {
                var buffer = new byte[81920];
                long received = 0, reported = 0;
                int read;

                while ((read = await source.ReadAsync(buffer, linked.Token).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), linked.Token).ConfigureAwait(false);
                    received += read;

                    // Repainting on every chunk would hammer the UI thread.
                    if (received - reported >= 1_000_000)
                    {
                        reported = received;
                        progress?.Report(total > 0 ? (double)received / total : 0);
                    }
                }
            }

            if (release.AssetSize > 0 && new FileInfo(partial).Length != release.AssetSize)
                throw new InvalidDataException(
                    $"size mismatch: expected {release.AssetSize} bytes, got {new FileInfo(partial).Length}");

            if (release.Sha256Url is not null)
                await VerifyHashAsync(partial, release.Sha256Url, linked.Token).ConfigureAwait(false);

            if (!LooksLikeExecutable(partial) || !MatchesVersion(partial, release.Version))
                throw new InvalidDataException("downloaded file is not the expected UnlockKit build");

            File.Move(partial, path, overwrite: true);
            progress?.Report(1);
            Log($"download: {path} ({new FileInfo(path).Length / 1048576.0:0.0} MB)");
            return path;
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    private static async Task VerifyHashAsync(string path, string shaUrl, CancellationToken ct)
    {
        var text = await ApiClient.Value.GetStringAsync(shaUrl, ct).ConfigureAwait(false);
        var expected = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                           .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(expected))
            throw new InvalidDataException("checksum file is empty");

        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));

        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"checksum mismatch: published {expected}, downloaded {actual}");
    }

    /// <summary>Cheap sanity check: an unsigned blob of HTML must never be run.</summary>
    private static bool LooksLikeExecutable(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return stream.Length > 1_000_000 && stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
        }
        catch
        {
            return false;
        }
    }

    private static bool MatchesVersion(string path, Version expected)
    {
        try
        {
            var actual = FileVersionInfo.GetVersionInfo(path).FileVersion;
            return actual is not null && actual.StartsWith(expected.ToString(3), StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    // ── applying ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Where the new build should land: the running exe when we may overwrite
    /// it, otherwise the per-user install folder (a build run from Program Files
    /// or a read-only share upgrades itself there instead of failing).
    /// </summary>
    public static string UpdateTarget()
    {
        var current = Installer.CurrentExe;
        if (CanReplace(current)) return current;

        Log($"update: cannot write next to {current}, targeting the install folder");
        Installer.InstallLocally();
        return Installer.InstalledExe;
    }

    /// <summary>
    /// Whether the file can be replaced once we exit. The file itself cannot be
    /// opened for writing while it is the running image - Windows refuses that
    /// for any executable - so the test is on the folder that will hold it.
    /// </summary>
    private static bool CanReplace(string path)
    {
        try
        {
            var folder = Path.GetDirectoryName(path);
            if (folder is null || !Directory.Exists(folder)) return false;

            var probe = Path.Combine(folder, $".unlockkit-{Guid.NewGuid():N}.tmp");
            using var stream = File.Create(probe, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Start the staged build with the arguments that make it swap itself in.</summary>
    public static void StartApply(string stagedExe, string targetExe, int waitForPid)
    {
        var start = new ProcessStartInfo
        {
            FileName = stagedExe,
            Arguments = $"--apply-update \"{targetExe}\" {waitForPid}",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(stagedExe) ?? Path.GetTempPath(),
        };

        Process.Start(start);
        Log($"apply: staged {stagedExe} -> {targetExe}, waiting for pid {waitForPid}");
    }

    /// <summary>
    /// Runs inside the downloaded build. Waits for the old process to release the
    /// image, copies itself over the target, relaunches it, and returns an exit code.
    /// </summary>
    public static int ApplyFromStaged(string targetExe, int waitForPid)
    {
        try
        {
            using var old = Process.GetProcessById(waitForPid);
            if (!old.WaitForExit(30_000)) Log("apply: old process did not exit within 30s, trying anyway");
        }
        catch (ArgumentException)
        {
            // Already gone - the common case when the user closed the window fast.
        }
        catch (Exception ex)
        {
            Log("apply: wait failed: " + ex.Message);
        }

        var self = Installer.CurrentExe;

        // The image stays locked for a moment after the process object signals.
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                if (!string.Equals(self, targetExe, StringComparison.OrdinalIgnoreCase))
                    File.Copy(self, targetExe, overwrite: true);

                Log($"apply: replaced {targetExe} with {CurrentVersionText}");
                Process.Start(new ProcessStartInfo
                {
                    FileName = targetExe,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(targetExe) ?? Path.GetTempPath(),
                });
                return 0;
            }
            catch (IOException) { Thread.Sleep(250); }
            catch (UnauthorizedAccessException) { Thread.Sleep(250); }
            catch (Exception ex)
            {
                Log("apply: " + ex.Message);
                break;
            }
        }

        Log($"apply: could not replace {targetExe}, relaunching the existing build");

        // Never leave the user with nothing running.
        try
        {
            Process.Start(new ProcessStartInfo { FileName = targetExe, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log("apply: relaunch failed: " + ex.Message);
        }

        return 1;
    }

    // ── reminder policy: at most once a day ───────────────────────────────────

    /// <summary>
    /// True when this version has not been skipped and the user has not already
    /// been told today. Skipping is per-version; a later release prompts again.
    /// </summary>
    public static bool ShouldPrompt(Version version)
    {
        if (ReadString("Update.Skipped") is { } skipped &&
            ParseVersion(skipped) is { } skipVersion &&
            skipVersion >= version)
            return false;

        return ReadString("Update.Prompted") != DateTime.Today.ToString("yyyy-MM-dd");
    }

    public static void MarkPrompted() => WriteString("Update.Prompted", DateTime.Today.ToString("yyyy-MM-dd"));

    public static void Skip(Version version) => WriteString("Update.Skipped", version.ToString(3));

    public static bool IsSkipped(Version version) =>
        ReadString("Update.Skipped") is { } skipped &&
        ParseVersion(skipped) is { } skipVersion &&
        skipVersion >= version;

    // ── housekeeping ─────────────────────────────────────────────────────────

    /// <summary>Drops staged binaries we are already running (or older ones).</summary>
    public static void CleanStaging()
    {
        try
        {
            if (!Directory.Exists(StagingDir)) return;

            foreach (var file in Directory.EnumerateFiles(StagingDir))
            {
                var name = Path.GetFileName(file);

                if (name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(file);
                    continue;
                }

                var version = name.StartsWith("UnlockKit-", StringComparison.OrdinalIgnoreCase)
                    ? ParseVersion(name["UnlockKit-".Length..^4])
                    : null;

                if (version is null || version <= CurrentVersion) TryDelete(file);
            }
        }
        catch
        {
            // A locked file just waits for the next run.
        }
    }

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(BaseDir);

            var file = new FileInfo(LogPath);
            if (file.Exists && file.Length > 256 * 1024) file.Delete();

            File.AppendAllText(LogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch
        {
            // Diagnostics must never break the feature.
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>Turns transport failures into something the user can act on.</summary>
    public static string Explain(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests } =>
            "GitHub is rate-limiting update checks from this machine (60 per hour). Try again later.",
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } =>
            "No release found for this repository.",
        HttpRequestException => "Could not reach GitHub. Check your connection or proxy.",
        OperationCanceledException => "The update check timed out.",
        _ => ex.Message,
    };

    /// <summary>Accepts "v1.2.0", "1.2", "1.2.0-beta.1" and normalises to four parts.</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var value = text.Trim().TrimStart('v', 'V');

        var dash = value.IndexOf('-');
        if (dash > 0) value = value[..dash];

        var plus = value.IndexOf('+');
        if (plus > 0) value = value[..plus];

        return Version.TryParse(value, out var parsed) ? Normalize(parsed) : null;
    }

    /// <summary>Version treats 1.2.0 as older than 1.2.0.0; pad so comparisons are sane.</summary>
    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build, v.Revision < 0 ? 0 : v.Revision);

    private static string? ReadString(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegPath);
            return key?.GetValue(name) as string;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteString(string name, string value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegPath);
            key?.SetValue(name, value);
        }
        catch
        {
            // Preferences are best-effort; the update still works.
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
