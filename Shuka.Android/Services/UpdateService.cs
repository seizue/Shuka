using System.Text.Json;
using System.Text.RegularExpressions;

namespace Shuka.Android.Services;

/// <summary>
/// Checks GitHub Releases for a newer APK and handles download + install.
/// Uses the public GitHub API — no auth token required.
/// </summary>
public static class UpdateService
{
    private const string ApiUrl     = "https://api.github.com/repos/seizue/Shuka/releases/latest";
    private const string UserAgent  = "Shuka-Android-Updater/1.0";
    private const string PrefKeyLastCheck = "update_last_check_utc";
    private const string PrefKeyLatestTag = "update_latest_tag";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    static UpdateService()
    {
        _http.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        _http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Current installed version from the app package.</summary>
    public static Version InstalledVersion
    {
        get
        {
#if ANDROID
            var ctx = global::Android.App.Application.Context;
            var info = ctx.PackageManager?.GetPackageInfo(ctx.PackageName ?? "", 0);
            string? vname = info?.VersionName;
            if (Version.TryParse(vname?.TrimStart('v'), out var v)) return v;
#endif
            // Fallback: read from assembly
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var ver = asm.GetName().Version;
            return ver ?? new Version(1, 0, 0);
        }
    }

    /// <summary>
    /// Fetches the latest release from GitHub.
    /// Returns null if the request fails or there is no APK asset.
    /// </summary>
    public static async Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await _http.GetStringAsync(ApiUrl, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tagName = root.GetProperty("tag_name").GetString() ?? "";
            string body    = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

            // Find the Android APK asset
            string? apkUrl  = null;
            long    apkSize = 0;

            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string name = asset.GetProperty("name").GetString() ?? "";
                    if (name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                    {
                        apkUrl  = asset.GetProperty("browser_download_url").GetString();
                        apkSize = asset.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                        break;
                    }
                }
            }

            if (apkUrl == null) return null;

            // Parse version from tag (e.g. "v1.2.3" or "v1.2.3.4")
            string vStr = tagName.TrimStart('v');
            if (!Version.TryParse(vStr, out var latestVersion)) return null;

            // Cache the latest tag so we can show badge without re-fetching
            Preferences.Default.Set(PrefKeyLatestTag, tagName);
            Preferences.Default.Set(PrefKeyLastCheck,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());

            return new ReleaseInfo(tagName, latestVersion, apkUrl, apkSize, body);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns true if a cached newer version is known without hitting the network.
    /// Used for the badge on app startup.
    /// </summary>
    public static bool HasCachedUpdate()
    {
        string tag = Preferences.Default.Get(PrefKeyLatestTag, "");
        if (string.IsNullOrWhiteSpace(tag)) return false;
        string vStr = tag.TrimStart('v');
        return Version.TryParse(vStr, out var v) && v > InstalledVersion;
    }

    /// <summary>
    /// Downloads the APK to the cache directory and triggers the system installer.
    /// Reports progress 0.0–1.0 via <paramref name="progress"/>.
    /// </summary>
    public static async Task DownloadAndInstallAsync(
        ReleaseInfo release,
        IProgress<double>? progress = null,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
#if ANDROID
        var ctx = global::Android.App.Application.Context;

        // Save to app-private cache — no storage permission needed
        string cacheDir = ctx.CacheDir!.AbsolutePath;
        string apkPath  = Path.Combine(cacheDir, $"shuka-update-{release.Tag}.apk");

        log?.Invoke($"Downloading {release.Tag} ({release.SizeMb:F1} MB)...");

        // Stream download with progress
        using var resp = await _http.GetAsync(release.ApkUrl,
            HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        long total = resp.Content.Headers.ContentLength ?? release.Size;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        await using var file   = File.Create(apkPath);

        var buffer  = new byte[81920];
        long downloaded = 0;
        int  read;

        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            downloaded += read;
            if (total > 0) progress?.Report((double)downloaded / total);
        }

        file.Close();
        log?.Invoke("Download complete. Launching installer...");

        // Trigger the system package installer
        InstallApk(ctx, apkPath);
#else
        await Task.CompletedTask;
        log?.Invoke("Auto-install is only supported on Android.");
#endif
    }

#if ANDROID
    private static void InstallApk(global::Android.Content.Context ctx, string apkPath)
    {
        // Use FileProvider on Android 7+ to share the file securely
        var apkFile = new Java.IO.File(apkPath);
        global::Android.Net.Uri uri;

        if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.N)
        {
            uri = AndroidX.Core.Content.FileProvider.GetUriForFile(
                ctx,
                $"{ctx.PackageName}.fileprovider",
                apkFile)!;
        }
        else
        {
            uri = global::Android.Net.Uri.FromFile(apkFile)!;
        }

        var intent = new global::Android.Content.Intent(
            global::Android.Content.Intent.ActionView);
        intent.SetDataAndType(uri, "application/vnd.android.package-archive");
        intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
        intent.AddFlags(global::Android.Content.ActivityFlags.GrantReadUriPermission);

        ctx.StartActivity(intent);
    }
#endif
}

/// <summary>Metadata for a GitHub release.</summary>
public record ReleaseInfo(
    string  Tag,
    Version Version,
    string  ApkUrl,
    long    Size,
    string  Notes)
{
    public double SizeMb => Size / 1_048_576.0;
    public bool IsNewerThan(Version installed) => Version > installed;
}
