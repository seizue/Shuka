using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidUri = Android.Net.Uri;
using Microsoft.Maui.Platform;
using AndroidX.Core.View;

namespace Shuka.Android;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges =
        ConfigChanges.ScreenSize | ConfigChanges.Orientation |
        ConfigChanges.UiMode | ConfigChanges.ScreenLayout |
        ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    public static MainActivity? Instance { get; private set; }
    public static bool PendingNavigationToDownloads = false;
    private int _navigationRetryCount = 0;
    private int _persistentTabBarHeightPx;
    private int _systemNavBarInsetPx;
    private float _swipeStartX;
    private float _swipeStartY;
    private bool _isSwipeTracking = false;
    private static bool _isSwipeNavigating = false;

    // Folder picker support
    public const int FolderPickerRequestCode = 9001;
    private TaskCompletionSource<AndroidUri?>? _folderPickerTcs;

    // File save/open picker support
    public const int FileSavePickerRequestCode = 9002;
    public const int FileOpenPickerRequestCode = 9003;
    private TaskCompletionSource<AndroidUri?>? _fileSavePickerTcs;
    private TaskCompletionSource<AndroidUri?>? _fileOpenPickerTcs;

    /// <summary>
    /// Opens the system folder picker and returns the selected tree URI, or null if cancelled.
    /// </summary>
    public Task<AndroidUri?> PickFolderAsync()
    {
        _folderPickerTcs = new TaskCompletionSource<AndroidUri?>();
        var intent = new Intent(Intent.ActionOpenDocumentTree);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
        StartActivityForResult(intent, FolderPickerRequestCode);
        return _folderPickerTcs.Task;
    }

    /// <summary>
    /// Opens the system file-save picker (CREATE_DOCUMENT) so the user can choose where
    /// to save a file with the given suggested name. Returns the chosen URI or null.
    /// </summary>
    public Task<AndroidUri?> PickSaveFileAsync(string suggestedName)
    {
        _fileSavePickerTcs = new TaskCompletionSource<AndroidUri?>();
        var intent = new Intent(Intent.ActionCreateDocument);
        intent.SetType("application/json");
        intent.PutExtra(Intent.ExtraTitle, suggestedName);
        intent.AddFlags(ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantReadUriPermission);
        StartActivityForResult(intent, FileSavePickerRequestCode);
        return _fileSavePickerTcs.Task;
    }

    /// <summary>
    /// Opens the system file-open picker (OPEN_DOCUMENT) filtered to JSON files.
    /// Returns the chosen URI or null.
    /// </summary>
    public Task<AndroidUri?> PickOpenFileAsync()
    {
        _fileOpenPickerTcs = new TaskCompletionSource<AndroidUri?>();
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.SetType("application/json");
        intent.AddCategory(Intent.CategoryOpenable);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission);
        StartActivityForResult(intent, FileOpenPickerRequestCode);
        return _fileOpenPickerTcs.Task;
    }

    /// <summary>
    /// Reads the entire contents of a content URI as a UTF-8 string.
    /// </summary>
    public string ReadUriToString(AndroidUri uri)
    {
        using var stream = ContentResolver!.OpenInputStream(uri)
            ?? throw new IOException("Cannot open URI for reading.");
        using var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Writes a UTF-8 string to a content URI (truncates any existing content).
    /// </summary>
    public void WriteStringToUri(AndroidUri uri, string content)
    {
        using var stream = ContentResolver!.OpenOutputStream(uri, "wt")
            ?? throw new IOException("Cannot open URI for writing.");
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);

        if (requestCode == FolderPickerRequestCode)
        {
            if (resultCode == Result.Ok && data?.Data is AndroidUri uri)
            {
                // Persist permission across reboots
                var flags = ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission;
                ContentResolver?.TakePersistableUriPermission(uri, flags);
                _folderPickerTcs?.TrySetResult(uri);
            }
            else
            {
                _folderPickerTcs?.TrySetResult(null);
            }
            _folderPickerTcs = null;
        }
        else if (requestCode == FileSavePickerRequestCode)
        {
            if (resultCode == Result.Ok && data?.Data is AndroidUri saveUri)
            {
                var flags = ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantReadUriPermission;
                ContentResolver?.TakePersistableUriPermission(saveUri, flags);
                _fileSavePickerTcs?.TrySetResult(saveUri);
            }
            else
            {
                _fileSavePickerTcs?.TrySetResult(null);
            }
            _fileSavePickerTcs = null;
        }
        else if (requestCode == FileOpenPickerRequestCode)
        {
            if (resultCode == Result.Ok && data?.Data is AndroidUri openUri)
            {
                _fileOpenPickerTcs?.TrySetResult(openUri);
            }
            else
            {
                _fileOpenPickerTcs?.TrySetResult(null);
            }
            _fileOpenPickerTcs = null;
        }
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Instance = this;
        HandleNotificationIntent(Intent);

#if DEBUG
        // Enable WebView debugging for logcat output
        global::Android.Webkit.WebView.SetWebContentsDebuggingEnabled(true);
        System.Diagnostics.Debug.WriteLine("[MainActivity] WebView debugging enabled");
#endif

        // Request POST_NOTIFICATIONS permission on Android 13+
#pragma warning disable CA1416
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
        {
            if (CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications)
                != global::Android.Content.PM.Permission.Granted)
            {
                RequestPermissions(
                    [global::Android.Manifest.Permission.PostNotifications], 1002);
            }
        }
#pragma warning restore CA1416

        var bgColor  = (Microsoft.Maui.Graphics.Color)Microsoft.Maui.Controls.Application.Current!.Resources["BgPage"];
        var navColor = (Microsoft.Maui.Graphics.Color)Microsoft.Maui.Controls.Application.Current!.Resources["NavBar"];
        bool lightIcons = App.CurrentTheme != AppTheme.Frost
                       && App.CurrentTheme != AppTheme.Parchment
                       && App.CurrentTheme != AppTheme.Blossom;

        var androidBg  = global::Android.Graphics.Color.Argb(
            (int)(bgColor.Alpha * 255), (int)(bgColor.Red * 255),
            (int)(bgColor.Green * 255), (int)(bgColor.Blue * 255));
        var androidNav = global::Android.Graphics.Color.Argb(
            (int)(navColor.Alpha * 255), (int)(navColor.Red * 255),
            (int)(navColor.Green * 255), (int)(navColor.Blue * 255));

        ApplyStatusBarColor(androidBg,  lightIcons);
        ApplyNavBarColor(androidNav, lightIcons);

        // Add the persistent tab bar as a native overlay on the DecorView.
        // This places it completely outside the MAUI/Shell/fragment hierarchy
        // so it is never affected by page transition animations.
        AddPersistentTabBar();
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        HandleNotificationIntent(intent);
    }

    protected override void OnResume()
    {
        base.OnResume();
        TriggerPendingNavigationIfAny();
    }

    private void HandleNotificationIntent(Intent? intent)
    {
        if (intent != null && intent.GetStringExtra("navigate_to") == "DownloadsPage")
        {
            intent.RemoveExtra("navigate_to"); // Avoid re-navigating on config changes/recreation
            PendingNavigationToDownloads = true;
            TriggerPendingNavigationIfAny();
        }
    }

    private void TriggerPendingNavigationIfAny()
    {
        if (PendingNavigationToDownloads)
        {
            if (Shell.Current != null)
            {
                PendingNavigationToDownloads = false;
                _navigationRetryCount = 0;
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    try
                    {
                        Controls.CustomTabBar.SetActive(1);
                        await Shell.Current.GoToAsync("//DownloadsPage");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MainActivity] Pending navigation failed: {ex.Message}");
                    }
                });
            }
            else if (_navigationRetryCount < 30) // Up to 3 seconds retry
            {
                _navigationRetryCount++;
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    await Task.Delay(100);
                    TriggerPendingNavigationIfAny();
                });
            }
            else
            {
                PendingNavigationToDownloads = false;
                _navigationRetryCount = 0;
            }
        }
    }

    private global::Android.Views.View? _persistentTabBar;

    /// <summary>
    /// Show or hide the persistent tab bar overlay.
    /// Call SetTabBarVisible(false) on pages that shouldn't show the tab bar (e.g. WebBrowsePage).
    /// </summary>
    public void SetTabBarVisible(bool visible)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_persistentTabBar != null)
                _persistentTabBar.Visibility = visible
                    ? global::Android.Views.ViewStates.Visible
                    : global::Android.Views.ViewStates.Gone;
        });
    }

    /// <summary>
    /// Inflates a single CustomTabBar into the DecorView's content frame so it
    /// sits above all pages and is never part of any fragment transaction.
    /// Respects the system navigation bar inset so it isn't covered by gesture/button nav.
    /// </summary>
    private void AddPersistentTabBar()
    {
        try
        {
            var decorContent = FindViewById<FrameLayout>(global::Android.Resource.Id.Content);
            if (decorContent == null) return;

            var mauiContext = Microsoft.Maui.Controls.Application.Current?.Handler?.MauiContext
                           ?? (Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()
                                  ?.Handler?.MauiContext);
            if (mauiContext == null) return;

            var tabBar = new Controls.CustomTabBar();
            var nativeTabBar = tabBar.ToPlatform(mauiContext);
            _persistentTabBar = nativeTabBar;

            float density  = Resources!.DisplayMetrics!.Density;
            int   tabBarPx = (int)(72 * density);
            _persistentTabBarHeightPx = tabBarPx;

            var lp = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                tabBarPx,
                GravityFlags.Bottom);

            decorContent.AddView(nativeTabBar, lp);

            // Apply window insets so the tab bar sits above the system nav bar,
            // not behind it. This handles both gesture nav and 3-button nav.
            ViewCompat.SetOnApplyWindowInsetsListener(nativeTabBar, new WindowInsetsCallback(this, lp, nativeTabBar, tabBarPx));
        }
        catch { /* never crash on tab bar setup */ }
    }

    private sealed class WindowInsetsCallback : Java.Lang.Object, AndroidX.Core.View.IOnApplyWindowInsetsListener
    {
        private readonly MainActivity _activity;
        private readonly FrameLayout.LayoutParams _lp;
        private readonly global::Android.Views.View _view;
        private readonly int _tabBarPx;

        public WindowInsetsCallback(MainActivity activity, FrameLayout.LayoutParams lp, global::Android.Views.View view, int tabBarPx)
        {
            _activity = activity;
            _lp       = lp;
            _view     = view;
            _tabBarPx = tabBarPx;
        }

        public AndroidX.Core.View.WindowInsetsCompat? OnApplyWindowInsets(
            global::Android.Views.View? v,
            AndroidX.Core.View.WindowInsetsCompat? insets)
        {
            if (insets == null) return insets;

            var navInsets    = insets!.GetInsets(AndroidX.Core.View.WindowInsetsCompat.Type.SystemBars());
            int navBarHeight = navInsets?.Bottom ?? 0;
            _activity._systemNavBarInsetPx = navBarHeight;

            // Sit the tab bar just above the system navigation bar
            _lp.BottomMargin       = navBarHeight;
            _lp.Height             = _tabBarPx;
            _view.LayoutParameters = _lp;

            return insets;
        }
    }

    /// <summary>
    /// Returns a recommended bottom inset (in MAUI dips) for overlays that should sit
    /// above the persistent tab bar and system navigation bar.
    /// </summary>
    public double GetOverlayBottomInsetDip(double extraDip = 16)
    {
        float density = Resources?.DisplayMetrics?.Density ?? 1f;
        double insetDip = (_persistentTabBarHeightPx + _systemNavBarInsetPx) / density;
        return insetDip + extraDip;
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus)
            StyleBottomNavigationView();
    }

    /// <summary>
    /// Finds the native BottomNavigationView in the view hierarchy and applies
    /// the pill-style active indicator matching the current Shuka theme.
    /// </summary>
    public void StyleBottomNavigationView()
    {
        try
        {
            var bottomNav = FindBottomNavigationView(Window?.DecorView);
            if (bottomNav == null) return;

            var app = Microsoft.Maui.Controls.Application.Current;
            if (app?.Resources == null) return;

            Microsoft.Maui.Graphics.Color accentBg   = app.Resources.TryGetValue("AccentContainer",  out var ab) ? (Microsoft.Maui.Graphics.Color)ab : Microsoft.Maui.Graphics.Color.FromArgb("#2A1E2E");
            Microsoft.Maui.Graphics.Color accent      = app.Resources.TryGetValue("AccentLight",       out var a)  ? (Microsoft.Maui.Graphics.Color)a  : Microsoft.Maui.Graphics.Color.FromArgb("#8B5E5F");
            Microsoft.Maui.Graphics.Color unselected  = app.Resources.TryGetValue("NavBarUnselected",  out var u)  ? (Microsoft.Maui.Graphics.Color)u  : Microsoft.Maui.Graphics.Color.FromArgb("#4A5270");
            Microsoft.Maui.Graphics.Color navBg       = app.Resources.TryGetValue("NavBar",            out var nb) ? (Microsoft.Maui.Graphics.Color)nb : Microsoft.Maui.Graphics.Color.FromArgb("#1A1D27");

            var androidAccentBg   = ToAndroidColor(accentBg);
            var androidAccent     = ToAndroidColor(accent);
            var androidUnselected = ToAndroidColor(unselected);
            var androidNavBg      = ToAndroidColor(navBg);

            bottomNav.SetBackgroundColor(androidNavBg);

            // Pill indicator color
            bottomNav.ItemActiveIndicatorEnabled = true;
            bottomNav.ItemActiveIndicatorColor   = global::Android.Content.Res.ColorStateList.ValueOf(androidAccentBg);

            // Icon + label tint
            var states   = new int[][] { [global::Android.Resource.Attribute.StateChecked], [] };
            var colors   = new int[] { androidAccent, androidUnselected };
            var tintList = new global::Android.Content.Res.ColorStateList(states, colors);
            bottomNav.ItemIconTintList = tintList;
            bottomNav.ItemTextColor    = tintList;

            // Remove ripple
            bottomNav.ItemRippleColor = global::Android.Content.Res.ColorStateList.ValueOf(global::Android.Graphics.Color.Transparent);

            // Always show labels
            bottomNav.LabelVisibilityMode = Google.Android.Material.BottomNavigation.LabelVisibilityMode.LabelVisibilityLabeled;
        }
        catch { /* never crash on styling */ }
    }

    private static Google.Android.Material.BottomNavigation.BottomNavigationView? FindBottomNavigationView(global::Android.Views.View? root)
    {
        if (root is Google.Android.Material.BottomNavigation.BottomNavigationView bnv)
            return bnv;
        if (root is global::Android.Views.ViewGroup vg)
        {
            for (int i = 0; i < vg.ChildCount; i++)
            {
                var found = FindBottomNavigationView(vg.GetChildAt(i));
                if (found != null) return found;
            }
        }
        return null;
    }

    private static global::Android.Graphics.Color ToAndroidColor(Microsoft.Maui.Graphics.Color c) =>
        global::Android.Graphics.Color.Argb(
            (int)(c.Alpha * 255), (int)(c.Red * 255),
            (int)(c.Green * 255), (int)(c.Blue * 255));

    /// <summary>
    /// Updates the status bar and navigation bar background and icon tint to match the current theme.
    /// </summary>
#pragma warning disable CA1416, CA1422
    public void ApplyStatusBarColor(global::Android.Graphics.Color bgColor, bool lightIcons)
    {
        if (Window is null) return;

        // ── Status bar ────────────────────────────────────────────────────────
        Window.SetStatusBarColor(bgColor);

        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
        {
            int appearance = 0;
            if (!lightIcons) appearance |= (int)WindowInsetsControllerAppearance.LightStatusBars;
            Window.InsetsController?.SetSystemBarsAppearance(
                appearance,
                (int)WindowInsetsControllerAppearance.LightStatusBars);
        }
        else if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
        {
            var flags = Window.DecorView.SystemUiFlags;
            Window.DecorView.SystemUiFlags = lightIcons
                ? flags & ~SystemUiFlags.LightStatusBar
                : flags | SystemUiFlags.LightStatusBar;
        }
    }

    /// <summary>
    /// Updates the bottom navigation bar (gesture bar / button bar) color and icon tint.
    /// Effective on API 26+ for icon tint, API 21+ for color.
    /// </summary>
    public void ApplyNavBarColor(global::Android.Graphics.Color navColor, bool lightIcons)
    {
        if (Window is null) return;

        Window.SetNavigationBarColor(navColor);

        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
        {
            // API 30+: WindowInsetsController
            int appearance = 0;
            if (!lightIcons) appearance |= (int)WindowInsetsControllerAppearance.LightNavigationBars;
            Window.InsetsController?.SetSystemBarsAppearance(
                appearance,
                (int)WindowInsetsControllerAppearance.LightNavigationBars);
        }
        else if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            // API 26–29: SystemUiFlags.LightNavigationBar
            var flags = Window.DecorView.SystemUiFlags;
            Window.DecorView.SystemUiFlags = lightIcons
                ? flags & ~SystemUiFlags.LightNavigationBar
                : flags | SystemUiFlags.LightNavigationBar;
        }
        // API 21–25: color set above, no icon tint control
    }
#pragma warning restore CA1416, CA1422

    // ── Platform Horizontal Swipe Navigation Interceptor ───────────────────

    public override bool DispatchTouchEvent(MotionEvent? ev)
    {
        if (ev == null) return base.DispatchTouchEvent(ev);

        switch (ev.ActionMasked)
        {
            case MotionEventActions.Down:
                _swipeStartX = ev.GetX();
                _swipeStartY = ev.GetY();
                _isSwipeTracking = IsCurrentPageTab();
                break;

            case MotionEventActions.Move:
                if (_isSwipeTracking)
                {
                    if (_isSwipeNavigating)
                        break; // Keep tracking but wait until transition completes

                    float diffX = ev.GetX() - _swipeStartX;
                    float diffY = ev.GetY() - _swipeStartY;

                    float density = Resources?.DisplayMetrics?.Density ?? 1f;
                    float threshold = 40 * density; // Responsive 40 dp gesture threshold

                    // Guard: If vertical scrolling is dominant and exceeds a small scroll threshold,
                    // disarm swipe tracking for this gesture sequence to avoid accidental tab switches.
                    if (Math.Abs(diffY) > 20 * density && Math.Abs(diffY) > Math.Abs(diffX) * 1.5f)
                    {
                        _isSwipeTracking = false;
                        break;
                    }

                    // Check if swipe distance is reached and horizontal action is dominant (more forgiving 1.2x ratio)
                    if (Math.Abs(diffX) > threshold && Math.Abs(diffX) > Math.Abs(diffY) * 1.2f)
                    {
                        int delta = diffX > 0 ? -1 : 1;
                        int currentIndex = Controls.CustomTabBar.ActiveIndex;
                        int targetIndex = currentIndex + delta;

                        if (targetIndex >= 0 && targetIndex < AppShell.TabRoutes.Length)
                        {
                            _isSwipeTracking = false; // Prevent multiple triggers in same stream
                            NavigateToTab(delta);

                            // Cleanly abort ongoing touch states in child views (like ScrollViews or buttons)
                            var cancelEvent = MotionEvent.Obtain(
                                ev.DownTime,
                                ev.EventTime,
                                MotionEventActions.Cancel,
                                ev.GetX(),
                                ev.GetY(),
                                0);
                            if (cancelEvent != null)
                            {
                                base.DispatchTouchEvent(cancelEvent);
                                cancelEvent.Recycle();
                            }

                            return true; // Consume this move event
                        }
                        else
                        {
                            // Out of bounds swipe (e.g. swipe right on home page or left on settings page)
                            // Kill tracking for this gesture to avoid spamming checks
                            _isSwipeTracking = false;
                        }
                    }
                }
                break;

            case MotionEventActions.Up:
            case MotionEventActions.Cancel:
                _isSwipeTracking = false;
                break;
        }

        return base.DispatchTouchEvent(ev);
    }

    public void NavigateToTab(int delta)
    {
        if (_isSwipeNavigating) return;

        if (!IsCurrentPageTab()) return;

        int currentIndex = Controls.CustomTabBar.ActiveIndex;
        int targetIndex = currentIndex + delta;
        if (targetIndex < 0 || targetIndex >= AppShell.TabRoutes.Length)
            return;

        _isSwipeNavigating = true;
        string route = "//" + AppShell.TabRoutes[targetIndex];

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                Controls.CustomTabBar.SetActive(targetIndex);
                await Shell.Current.GoToAsync(route);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SwipeNavigation] Platform swipe failed: {ex.Message}");
            }
            finally
            {
                // Delay buffer matching tab transition duration to prevent double-swiping
                await Task.Delay(300); // Snappy 300ms lock-out
                _isSwipeNavigating = false;
            }
        });
    }

    private static bool IsCurrentPageTab()
    {
        var currentPage = Shell.Current?.CurrentPage;
        if (currentPage == null) return false;

        string name = currentPage.GetType().Name;
        return name is "MainPage" or "DownloadsPage" or "HistoryPage" or "SettingsPage";
    }
}
