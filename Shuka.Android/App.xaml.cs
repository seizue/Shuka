using Shuka.Android.Services;

namespace Shuka.Android;

public enum AppTheme { Obsidian, Rosewood, Slate, Frost }

public partial class App : Application
{
    public static AppTheme CurrentTheme { get; private set; } = AppTheme.Obsidian;

    public App()
    {
        InitializeComponent();
        var saved = Preferences.Default.Get("app_theme", nameof(AppTheme.Slate));
        if (saved == "Parchment") saved = nameof(AppTheme.Frost);
        var theme = Enum.TryParse<AppTheme>(saved, out var t) ? t : AppTheme.Slate;
        ApplyTheme(theme);

        // Background update check — runs once per session, doesn't block startup
        _ = CheckForUpdateSilentlyAsync();
    }

    protected override Window CreateWindow(IActivationState? activationState)
        => new Window(new AppShell());

    // ── Silent update check ───────────────────────────────────────────────────

    private static async Task CheckForUpdateSilentlyAsync()
    {
        // Throttle: only check once every 6 hours
        const long checkIntervalSec = 6 * 3600;
        string lastCheckStr = Preferences.Default.Get("update_last_check_utc", "0");
        long lastCheck = long.TryParse(lastCheckStr, out long lc) ? lc : 0;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (now - lastCheck < checkIntervalSec) return;

        try
        {
            var release = await UpdateService.GetLatestReleaseAsync();
            if (release == null) return;
            if (!release.IsNewerThan(UpdateService.InstalledVersion)) return;

            // Post a system notification
            PostUpdateNotification(release);
        }
        catch { /* silent — never crash on background check */ }
    }

    private static void PostUpdateNotification(ReleaseInfo release)
    {
#if ANDROID
        const string ChannelId = "shuka_update_channel";
        var ctx = global::Android.App.Application.Context;

        // Ensure notification channel exists (Android 8+)
        if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.O)
        {
#pragma warning disable CA1416
            var nm = (global::Android.App.NotificationManager?)
                ctx.GetSystemService(global::Android.Content.Context.NotificationService);
            if (nm?.GetNotificationChannel(ChannelId) == null)
            {
                var ch = new global::Android.App.NotificationChannel(
                    ChannelId, "App Updates",
                    global::Android.App.NotificationImportance.Default)
                {
                    Description = "Notifies when a new version of Shuka is available"
                };
                nm?.CreateNotificationChannel(ch);
            }
#pragma warning restore CA1416
        }

        // Build tap intent — opens the app to Settings tab
        var launchIntent = ctx.PackageManager
            ?.GetLaunchIntentForPackage(ctx.PackageName ?? "")
            ?.SetFlags(global::Android.Content.ActivityFlags.SingleTop)
            ?? new global::Android.Content.Intent(ctx,
                   typeof(global::Shuka.Android.MainActivity));

#pragma warning disable CA1416
        var pendingFlags = global::Android.OS.Build.VERSION.SdkInt >=
                           global::Android.OS.BuildVersionCodes.M
            ? global::Android.App.PendingIntentFlags.UpdateCurrent |
              global::Android.App.PendingIntentFlags.Immutable
            : global::Android.App.PendingIntentFlags.UpdateCurrent;
#pragma warning restore CA1416

        var pi = global::Android.App.PendingIntent.GetActivity(
            ctx, 0, launchIntent, pendingFlags);

        var notification = new AndroidX.Core.App.NotificationCompat.Builder(ctx, ChannelId)
            .SetContentTitle("Shuka update available")
            .SetContentText($"v{release.Version} is ready to install")
            .SetSmallIcon(global::Android.Resource.Drawable.StatSysDownload)
            .SetAutoCancel(true)
#pragma warning disable CS8604
            .SetContentIntent(pi)
#pragma warning restore CS8604
            .SetPriority(AndroidX.Core.App.NotificationCompat.PriorityDefault)
            .Build()!;

        var mgr = AndroidX.Core.App.NotificationManagerCompat.From(ctx);
        mgr?.Notify(9999, notification);
#endif
    }

    public static void ApplyTheme(AppTheme theme)
    {
        CurrentTheme = theme;
        var r = Application.Current!.Resources;
        // Persist selection
        Preferences.Default.Set("app_theme", theme.ToString());

        // Accent #533738 is constant — only surfaces, text, and semantic colors shift
        switch (theme)
        {
            // ── Obsidian ─────────────────────────────────────────────────────
            // Warm Apple-style charcoal. Clean, modern, accent pops.
            case AppTheme.Obsidian:
                r["BgPage"]           = Color.FromArgb("#1C1C1E");
                r["BgCard"]           = Color.FromArgb("#2C2C2E");
                r["BgInput"]          = Color.FromArgb("#2C2C2E");
                r["Stroke"]           = Color.FromArgb("#48484A");
                r["Divider"]          = Color.FromArgb("#38383A");
                r["EntryLine"]        = Color.FromArgb("#48484A");
                r["EntryLineFocused"] = Color.FromArgb("#8B5E5F");
                r["Accent"]           = Color.FromArgb("#533738");
                r["AccentLight"]      = Color.FromArgb("#8B5E5F");
                r["AccentContainer"]  = Color.FromArgb("#2E1F1F");
                r["Success"]          = Color.FromArgb("#30D158");
                r["SuccessContainer"] = Color.FromArgb("#0D2E18");
                r["Warning"]          = Color.FromArgb("#FFD60A");
                r["Danger"]           = Color.FromArgb("#FF453A");
                r["TextPrimary"]      = Color.FromArgb("#F2F2F7");
                r["TextSecondary"]    = Color.FromArgb("#AEAEB2");
                r["TextMuted"]        = Color.FromArgb("#636366");
                r["TextOnAccent"]     = Color.FromArgb("#F2F2F7");
                r["ProgressTrack"]    = Color.FromArgb("#3A3A3C");
                r["NavBar"]           = Color.FromArgb("#2C2C2E");
                r["NavBarSelected"]   = Color.FromArgb("#8B5E5F");
                r["NavBarUnselected"] = Color.FromArgb("#636366");
                break;

            // ── Rosewood ─────────────────────────────────────────────────────
            // Warm brown-dark surfaces. Harmonizes with the red undertone.
            case AppTheme.Rosewood:
                r["BgPage"]           = Color.FromArgb("#1A1614");
                r["BgCard"]           = Color.FromArgb("#261E1C");
                r["BgInput"]          = Color.FromArgb("#261E1C");
                r["Stroke"]           = Color.FromArgb("#42302E");
                r["Divider"]          = Color.FromArgb("#2E2220");
                r["EntryLine"]        = Color.FromArgb("#42302E");
                r["EntryLineFocused"] = Color.FromArgb("#8B5E5F");
                r["Accent"]           = Color.FromArgb("#533738");
                r["AccentLight"]      = Color.FromArgb("#8B5E5F");
                r["AccentContainer"]  = Color.FromArgb("#3D2422");
                r["Success"]          = Color.FromArgb("#4CAF72");
                r["SuccessContainer"] = Color.FromArgb("#1A3326");
                r["Warning"]          = Color.FromArgb("#E8B84B");
                r["Danger"]           = Color.FromArgb("#E05C52");
                r["TextPrimary"]      = Color.FromArgb("#F5EDEB");
                r["TextSecondary"]    = Color.FromArgb("#C4AEA9");
                r["TextMuted"]        = Color.FromArgb("#6E5550");
                r["TextOnAccent"]     = Color.FromArgb("#F5EDEB");
                r["ProgressTrack"]    = Color.FromArgb("#332624");
                r["NavBar"]           = Color.FromArgb("#261E1C");
                r["NavBarSelected"]   = Color.FromArgb("#8B5E5F");
                r["NavBarUnselected"] = Color.FromArgb("#6E5550");
                break;

            // ── Slate ─────────────────────────────────────────────────────────
            // Cool blue-grey. High contrast — warm accent stands out sharply.
            case AppTheme.Slate:
                r["BgPage"]           = Color.FromArgb("#0F1117");
                r["BgCard"]           = Color.FromArgb("#1A1D27");
                r["BgInput"]          = Color.FromArgb("#1A1D27");
                r["Stroke"]           = Color.FromArgb("#2E3245");
                r["Divider"]          = Color.FromArgb("#22253A");
                r["EntryLine"]        = Color.FromArgb("#2E3245");
                r["EntryLineFocused"] = Color.FromArgb("#8B5E5F");
                r["Accent"]           = Color.FromArgb("#533738");
                r["AccentLight"]      = Color.FromArgb("#8B5E5F");
                r["AccentContainer"]  = Color.FromArgb("#2A1E2E");
                r["Success"]          = Color.FromArgb("#4ADE80");
                r["SuccessContainer"] = Color.FromArgb("#0D2A1A");
                r["Warning"]          = Color.FromArgb("#FACC15");
                r["Danger"]           = Color.FromArgb("#F87171");
                r["TextPrimary"]      = Color.FromArgb("#E8EAF6");
                r["TextSecondary"]    = Color.FromArgb("#9FA8C0");
                r["TextMuted"]        = Color.FromArgb("#4A5270");
                r["TextOnAccent"]     = Color.FromArgb("#E8EAF6");
                r["ProgressTrack"]    = Color.FromArgb("#252836");
                r["NavBar"]           = Color.FromArgb("#1A1D27");
                r["NavBarSelected"]   = Color.FromArgb("#8B5E5F");
                r["NavBarUnselected"] = Color.FromArgb("#4A5270");
                break;

            // ── Frost ─────────────────────────────────────────────────────────
            // Modern iOS-style light. Pure white cards on soft grey, burgundy accent.
            case AppTheme.Frost:
                r["BgPage"]           = Color.FromArgb("#F2F2F7");  // iOS system grouped background
                r["BgCard"]           = Color.FromArgb("#FFFFFF");  // pure white cards
                r["BgInput"]          = Color.FromArgb("#FFFFFF");  // white input bg
                r["Stroke"]           = Color.FromArgb("#E5E5EA");  // iOS separator grey
                r["Divider"]          = Color.FromArgb("#E5E5EA");
                r["EntryLine"]        = Color.FromArgb("#C7C7CC");
                r["EntryLineFocused"] = Color.FromArgb("#533738");
                r["Accent"]           = Color.FromArgb("#533738");  // burgundy
                r["AccentLight"]      = Color.FromArgb("#7A4E4F");
                r["AccentContainer"]  = Color.FromArgb("#F2E8E8");  // very light blush
                r["Success"]          = Color.FromArgb("#34C759");  // iOS green
                r["SuccessContainer"] = Color.FromArgb("#E8F8ED");
                r["Warning"]          = Color.FromArgb("#FF9500");  // iOS orange
                r["Danger"]           = Color.FromArgb("#FF3B30");  // iOS red
                r["TextPrimary"]      = Color.FromArgb("#000000");  // pure black
                r["TextSecondary"]    = Color.FromArgb("#3C3C43");  // iOS label secondary
                r["TextMuted"]        = Color.FromArgb("#8E8E93");  // iOS tertiary label
                r["TextOnAccent"]     = Color.FromArgb("#FFFFFF");
                r["ProgressTrack"]    = Color.FromArgb("#E5E5EA");
                r["NavBar"]           = Color.FromArgb("#FFFFFF");  // pure white bar — clean separation from page
                r["NavBarSelected"]   = Color.FromArgb("#533738");  // burgundy accent — clear active state
                r["NavBarUnselected"] = Color.FromArgb("#AEAEB2");  // soft grey — visible but not competing
                break;
        }

        // Re-tint all active Entry underlines to match the new theme
#if ANDROID
        Platforms.Android.ThemedEntryHandler.RefreshAll();

        // Update the system status bar to match the page background
        if (MainActivity.Instance is { } activity)
        {
            var bgColor = (Color)Application.Current!.Resources["BgPage"];
            // Light icons for dark themes, dark icons for Frost (light theme)
            bool lightIcons = theme != AppTheme.Frost;
            var androidColor = global::Android.Graphics.Color.Argb(
                (int)(bgColor.Alpha * 255),
                (int)(bgColor.Red   * 255),
                (int)(bgColor.Green * 255),
                (int)(bgColor.Blue  * 255));
            MainThread.BeginInvokeOnMainThread(() =>
                activity.ApplyStatusBarColor(androidColor, lightIcons));
        }
#endif
    }
}
