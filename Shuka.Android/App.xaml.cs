using Microsoft.Maui.Layouts;
using Shuka.Android.Services;

namespace Shuka.Android;

public enum AppTheme { Obsidian, Rosewood, Slate, Frost, Amoled, Parchment, Blossom }

public partial class App : Application
{
    public static AppTheme CurrentTheme { get; private set; } = AppTheme.Obsidian;
    private const string PrefKeyLastNotifiedTag = "update_last_notified_tag";
    private static readonly TimeSpan UpdatePollInterval = TimeSpan.FromHours(6);

    public App()
    {
        InitializeComponent();
        
        // Eagerly load reading history in the background so it is ready when navigated to
        _ = HistoryService.Instance;
        
        // Set up global exception handlers
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        
        var saved = Preferences.Default.Get("app_theme", nameof(AppTheme.Slate));
        if (saved == "Parchment") saved = nameof(AppTheme.Frost);
        var theme = Enum.TryParse<AppTheme>(saved, out var t) ? t : AppTheme.Slate;
        ApplyTheme(theme);

        // Background update checks — run in a low-frequency loop while app is alive.
        _ = RunBackgroundUpdateLoopAsync();
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] Unhandled exception: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[App] Stack trace: {ex.StackTrace}");
            
            // Log to a file for debugging
            try
            {
                var logPath = Path.Combine(FileSystem.CacheDirectory, "crash.log");
                var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n\n";
                File.AppendAllText(logPath, logEntry);
            }
            catch { /* ignore logging errors */ }
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"[App] Unobserved task exception: {e.Exception.Message}");
        System.Diagnostics.Debug.WriteLine($"[App] Stack trace: {e.Exception.StackTrace}");
        
        // Log to a file for debugging
        try
        {
            var logPath = Path.Combine(FileSystem.CacheDirectory, "crash.log");
            var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Unobserved: {e.Exception.GetType().Name}: {e.Exception.Message}\n{e.Exception.StackTrace}\n\n";
            File.AppendAllText(logPath, logEntry);
        }
        catch { /* ignore logging errors */ }
        
        e.SetObserved(); // Prevent app crash
    }

    protected override Window CreateWindow(IActivationState? activationState)
        => new Window(new AppShell());

    // ── Silent update checks ──────────────────────────────────────────────────

    private static async Task RunBackgroundUpdateLoopAsync()
    {
        // Small startup delay so initial UI stays snappy.
        await Task.Delay(TimeSpan.FromSeconds(8));
        while (true)
        {
            try
            {
                await CheckForUpdateSilentlyAsync();
            }
            catch
            {
                // Never break the loop because of updater errors.
            }

            await Task.Delay(UpdatePollInterval);
        }
    }

    private static async Task CheckForUpdateSilentlyAsync()
    {
        // Throttle: only check once every 6 hours
        const long checkIntervalSec = 6 * 3600;
        string lastCheckStr = Preferences.Default.Get("update_last_check_utc", "0");
        long lastCheck = long.TryParse(lastCheckStr, out long lc) ? lc : 0;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (now - lastCheck < checkIntervalSec) return;
        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet) return;

        try
        {
            var release = await UpdateService.GetLatestReleaseAsync();
            if (release == null) return;
            if (!release.IsNewerThan(UpdateService.InstalledVersion)) return;

            // Don't spam notifications for the same tag.
            string lastNotified = Preferences.Default.Get(PrefKeyLastNotifiedTag, "");
            if (string.Equals(lastNotified, release.Tag, StringComparison.OrdinalIgnoreCase))
                return;

            // Post a system notification
            PostUpdateNotification(release);
            Preferences.Default.Set(PrefKeyLastNotifiedTag, release.Tag);
        }
        catch { /* silent — never crash on background check */ }
    }

    private static void PostUpdateNotification(ReleaseInfo release)
    {
#if ANDROID
        const string ChannelId = "shuka_update_channel";
        var ctx = global::Android.App.Application.Context;
        if (ctx is null) return;

        // Ensure notification channel exists (Android 8+)
        if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.O)
        {
#pragma warning disable CA1416, CS8602
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
#pragma warning restore CA1416, CS8602
        }

        // Build tap intent — opens GitHub release page directly.
        var launchIntent = new global::Android.Content.Intent(
            global::Android.Content.Intent.ActionView,
            global::Android.Net.Uri.Parse(
                string.IsNullOrWhiteSpace(release.ReleasePageUrl)
                    ? UpdateService.ReleasesPageUrl
                    : release.ReleasePageUrl));
        launchIntent.AddFlags(global::Android.Content.ActivityFlags.NewTask);

#pragma warning disable CA1416
        var pendingFlags = global::Android.OS.Build.VERSION.SdkInt >=
                           global::Android.OS.BuildVersionCodes.M
            ? global::Android.App.PendingIntentFlags.UpdateCurrent |
              global::Android.App.PendingIntentFlags.Immutable
            : global::Android.App.PendingIntentFlags.UpdateCurrent;
#pragma warning restore CA1416

        var pi = global::Android.App.PendingIntent.GetActivity(
            ctx, 0, launchIntent, pendingFlags);

#pragma warning disable CS8602
        var notification = new AndroidX.Core.App.NotificationCompat.Builder(ctx, ChannelId)
#pragma warning restore CS8602
            .SetContentTitle("Shuka update available")
            .SetContentText($"v{release.Version} is available - tap to view release")
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
                r["NavBar"]           = Color.FromArgb("#FFFFFF");  // pure white bar
                r["NavBarSelected"]   = Color.FromArgb("#533738");
                r["NavBarUnselected"] = Color.FromArgb("#AEAEB2");
                break;

            // ── Amoled ────────────────────────────────────────────────────────
            // True black — saves battery on OLED screens. Maximum contrast.
            case AppTheme.Amoled:
                r["BgPage"]           = Color.FromArgb("#000000");  // true black
                r["BgCard"]           = Color.FromArgb("#0D0D0D");  // near-black cards
                r["BgInput"]          = Color.FromArgb("#0D0D0D");
                r["Stroke"]           = Color.FromArgb("#1C1C1C");
                r["Divider"]          = Color.FromArgb("#141414");
                r["EntryLine"]        = Color.FromArgb("#1C1C1C");
                r["EntryLineFocused"] = Color.FromArgb("#8B5E5F");
                r["Accent"]           = Color.FromArgb("#533738");
                r["AccentLight"]      = Color.FromArgb("#8B5E5F");
                r["AccentContainer"]  = Color.FromArgb("#1A0F0F");
                r["Success"]          = Color.FromArgb("#30D158");
                r["SuccessContainer"] = Color.FromArgb("#0A1F10");
                r["Warning"]          = Color.FromArgb("#FFD60A");
                r["Danger"]           = Color.FromArgb("#FF453A");
                r["TextPrimary"]      = Color.FromArgb("#FFFFFF");  // pure white text
                r["TextSecondary"]    = Color.FromArgb("#EBEBF5");
                r["TextMuted"]        = Color.FromArgb("#545458");
                r["TextOnAccent"]     = Color.FromArgb("#FFFFFF");
                r["ProgressTrack"]    = Color.FromArgb("#1C1C1C");
                r["NavBar"]           = Color.FromArgb("#000000");  // true black nav bar
                r["NavBarSelected"]   = Color.FromArgb("#8B5E5F");
                r["NavBarUnselected"] = Color.FromArgb("#545458");
                break;

            // ── Parchment ─────────────────────────────────────────────────────
            // Warm cream — aged paper feel. Easy on the eyes for long reading.
            case AppTheme.Parchment:
                r["BgPage"]           = Color.FromArgb("#FBF4E2");  // warm cream
                r["BgCard"]           = Color.FromArgb("#FFF8EC");  // lighter cream cards
                r["BgInput"]          = Color.FromArgb("#FFF8EC");
                r["Stroke"]           = Color.FromArgb("#E8D9B8");  // warm tan border
                r["Divider"]          = Color.FromArgb("#EDE0C4");
                r["EntryLine"]        = Color.FromArgb("#D4BC90");
                r["EntryLineFocused"] = Color.FromArgb("#533738");
                r["Accent"]           = Color.FromArgb("#533738");  // burgundy
                r["AccentLight"]      = Color.FromArgb("#7A4E4F");
                r["AccentContainer"]  = Color.FromArgb("#F2DDD0");  // warm blush
                r["Success"]          = Color.FromArgb("#3A7D44");
                r["SuccessContainer"] = Color.FromArgb("#D6EDD9");
                r["Warning"]          = Color.FromArgb("#B45309");
                r["Danger"]           = Color.FromArgb("#C0392B");
                r["TextPrimary"]      = Color.FromArgb("#2C1A0E");  // deep warm brown
                r["TextSecondary"]    = Color.FromArgb("#5C3D2E");
                r["TextMuted"]        = Color.FromArgb("#A08060");
                r["TextOnAccent"]     = Color.FromArgb("#FBF4E2");
                r["ProgressTrack"]    = Color.FromArgb("#E8D9B8");
                r["NavBar"]           = Color.FromArgb("#FFF8EC");
                r["NavBarSelected"]   = Color.FromArgb("#533738");
                r["NavBarUnselected"] = Color.FromArgb("#A08060");
                break;

            // ── Blossom ───────────────────────────────────────────────────────
            // Soft pink — delicate and warm. Light feminine aesthetic.
            case AppTheme.Blossom:
                r["BgPage"]           = Color.FromArgb("#FEF2F6");  // soft pink
                r["BgCard"]           = Color.FromArgb("#FFFFFF");  // white cards
                r["BgInput"]          = Color.FromArgb("#FFFFFF");
                r["Stroke"]           = Color.FromArgb("#F5C6D8");  // pink border
                r["Divider"]          = Color.FromArgb("#FAD9E7");
                r["EntryLine"]        = Color.FromArgb("#EBA8C3");
                r["EntryLineFocused"] = Color.FromArgb("#533738");
                r["Accent"]           = Color.FromArgb("#533738");  // burgundy
                r["AccentLight"]      = Color.FromArgb("#7A4E4F");
                r["AccentContainer"]  = Color.FromArgb("#FADADD");  // light rose
                r["Success"]          = Color.FromArgb("#2E7D52");
                r["SuccessContainer"] = Color.FromArgb("#D4EDDA");
                r["Warning"]          = Color.FromArgb("#C07000");
                r["Danger"]           = Color.FromArgb("#C0392B");
                r["TextPrimary"]      = Color.FromArgb("#2D1B22");  // deep rose-brown
                r["TextSecondary"]    = Color.FromArgb("#5C3347");
                r["TextMuted"]        = Color.FromArgb("#B07090");
                r["TextOnAccent"]     = Color.FromArgb("#FEF2F6");
                r["ProgressTrack"]    = Color.FromArgb("#F5C6D8");
                r["NavBar"]           = Color.FromArgb("#FFFFFF");
                r["NavBarSelected"]   = Color.FromArgb("#533738");
                r["NavBarUnselected"] = Color.FromArgb("#B07090");
                break;
        }

        // Re-tint all active Entry underlines to match the new theme
#if ANDROID
        Platforms.Android.ThemedEntryHandler.RefreshAll();
        Platforms.Android.PillBottomNavTracker.RefreshAll();

        // Re-style the native BottomNavigationView pill indicator
        if (MainActivity.Instance is { } activity2)
            MainThread.BeginInvokeOnMainThread(() => activity2.StyleBottomNavigationView());
#endif

        // Refresh custom MAUI tab bar colors
        MainThread.BeginInvokeOnMainThread(() =>
            Controls.CustomTabBar.RefreshAll());

#if ANDROID
        // Update the system status bar and navigation bar to match the theme
        if (MainActivity.Instance is { } activity)
        {
            var bgColor  = (Color)Application.Current!.Resources["BgPage"];
            var navColor = (Color)Application.Current!.Resources["NavBar"];

            // Light icons for dark themes, dark icons for light themes
            bool lightIcons = theme != AppTheme.Frost
                           && theme != AppTheme.Parchment
                           && theme != AppTheme.Blossom;

            var androidBg = ToAndroidColor(bgColor);
            var androidNav = ToAndroidColor(navColor);

            MainThread.BeginInvokeOnMainThread(() =>
            {
                activity.ApplyStatusBarColor(androidBg,  lightIcons);
                activity.ApplyNavBarColor(androidNav, lightIcons);
            });
        }
#endif
    }

#if ANDROID
    private static global::Android.Graphics.Color ToAndroidColor(Color c) =>
        global::Android.Graphics.Color.Argb(
            (int)(c.Alpha * 255),
            (int)(c.Red   * 255),
            (int)(c.Green * 255),
            (int)(c.Blue  * 255));
#endif
}