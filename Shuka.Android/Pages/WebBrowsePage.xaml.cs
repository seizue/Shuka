using System.Text.Json;
using System.Text.RegularExpressions;
using Shuka.Android.Platforms.Android;
using Shuka.Android.Services;
using Shuka.Core.Adapters;
using Shuka.Core;
using Shuka.Android.Platform;

namespace Shuka.Android.Pages;

/// <summary>
/// Full-screen WebView browser for Discover sources.
/// Shows a floating Download FAB whenever the current URL matches a known
/// novel adapter (quanben.io, czbooks.net, dmxs.org, 69shuba.com, 52shuku.net).
/// </summary>
public partial class WebBrowsePage : ContentPage
{
    private sealed class BrowserTab
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public string Url { get; set; } = "";
        public string Title { get; set; } = "";
        public DateTime LastTouchedAt { get; set; } = DateTime.Now;
    }

    private sealed class WebVisitEntry
    {
        public string Url { get; init; } = "";
        public string Source { get; init; } = "";
        public string? Title { get; init; }
        public DateTime VisitedAt { get; init; } = DateTime.Now;
    }

    // Stricter per-site checks: is this URL a novel index page (not just the domain)?
    private static readonly Dictionary<string, Func<string, bool>> _novelPageChecks = new()
    {
        // quanben.io: /n/{bookId}/  or  /n/{bookId}/list.html
        ["quanben.io"] = url => System.Text.RegularExpressions.Regex.IsMatch(
            url, @"quanben\.io/n/[a-zA-Z0-9_\-]+/?", System.Text.RegularExpressions.RegexOptions.IgnoreCase),

        // czbooks.net: /n/{bookId}  (not /new/, /hot/, /search, etc.)
        ["czbooks.net"] = url => System.Text.RegularExpressions.Regex.IsMatch(
            url, @"czbooks\.net/n/[a-zA-Z0-9_\-]+/?", System.Text.RegularExpressions.RegexOptions.IgnoreCase),

        // 69shuba.com: /book/{numericId}/ or /book/{numericId}.htm
        ["69shuba.com"] = url => System.Text.RegularExpressions.Regex.IsMatch(
            url, @"69shuba\.com/book/\d+(?:\.htm)?/?", System.Text.RegularExpressions.RegexOptions.IgnoreCase),

        // dmxs.org: /{category}/{numericId}.html  (not /news_last/, /tags, etc.)
        ["dmxs.org"] = url => System.Text.RegularExpressions.Regex.IsMatch(
            url, @"dmxs\.org/[a-zA-Z]+/\d+\.html", System.Text.RegularExpressions.RegexOptions.IgnoreCase),

        // 52shuku.net: /{category}/{folder}/bk{id}.html (exclude recommendations & year recommendation links)
        ["52shuku.net"] = url => !url.Contains("/tuijian/", System.StringComparison.OrdinalIgnoreCase)
            && !url.Contains("_top", System.StringComparison.OrdinalIgnoreCase)
            && !System.Text.RegularExpressions.Regex.IsMatch(url, @"\d{4}年", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            && System.Text.RegularExpressions.Regex.IsMatch(
                url, @"52shuku\.net/[^/]+/[^/]+/bk[^/]+\.html", System.Text.RegularExpressions.RegexOptions.IgnoreCase),

        // situu.cc: /85_85861/
        ["situu.cc"] = url => System.Text.RegularExpressions.Regex.IsMatch(
            url, @"situu\.cc/\d+_\d+/?(?:[?#]|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase),

        // yamibo.com: /novel/{id}
        ["yamibo.com"] = url => System.Text.RegularExpressions.Regex.IsMatch(
            url, @"yamibo\.com/novel/\d+/?(?:[?#]|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase),

        // noveldex.io: /series/{slug} or /series/novel/{slug} (not /series?..., not /chapter/)
        ["noveldex.io"] = url => System.Text.RegularExpressions.Regex.IsMatch(
            url, @"noveldex\.io/series/(?:[^/]+/)*[a-zA-Z0-9_\-]+/?(?:[?#]|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase) &&
            !url.Contains("/chapter/", StringComparison.OrdinalIgnoreCase) &&
            !System.Text.RegularExpressions.Regex.IsMatch(url, @"noveldex\.io/series/?(?:[?#]|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase),

        // shubaow.net: /book/{numericId}.html or /{category}/{numericId}/
        ["shubaow.net"] = url => System.Text.RegularExpressions.Regex.IsMatch(
            url, @"shubaow\.net/(?:book/\d+\.html|\d+/\d+/?)(?:[?#]|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase),
    };

    /// <summary>Returns true if the URL is a valid novel index page for its site.</summary>
    private static bool IsNovelPage(string url)
    {
        string? site = DetectSite(url);
        if (site == null) return false;
        return _novelPageChecks.TryGetValue(site, out var check) && check(url);
    }

    private enum WebTranslateMode { None, GoogleProxy }

    private string _currentUrl;
    private readonly string _homeUrl;
    private bool _isLoading;
    /// <summary>Google proxy loads translate.google.com; <see cref="_originalUrl"/> tracks the embedded site URL for re-translate.</summary>
    private WebTranslateMode _translateMode;
    /// <summary>Real page URL behind the Google Translate wrapper (updated when you follow links while translated).</summary>
    private string _originalUrl = string.Empty;
    private int _translateEmbeddedSyncGeneration;
    /// <summary>
    /// Always the last real <c>e.Url</c> from WebView Navigating/Navigated (never a “hoped for” target during leave-translate).
    /// Used with the address bar to detect if we are still on Google Translate.
    /// </summary>
    private string _actualWebNavigationUrl = string.Empty;
    private bool _fabMenuExpanded = false; // tracks FAB menu state
    private readonly List<BrowserTab> _tabs = new();
    private Guid _activeTabId;
    private List<WebVisitEntry> _recentVisits = new();
    private bool _isTabOverviewOpen;
    private bool _isBrowserMenuOpen;
    private bool _isRecentLinksOpen;
    private bool _isCloudflareSheetOpen;
    private bool _isImageContextMenuOpen;
    private string? _currentImageContextMenuUrl;
    private string? _currentLinkContextMenuUrl;
    private bool _longClickListenerAttached;
    private TaskCompletionSource<string?>? _cloudflareChoiceTcs;
    private static string WebHistoryFile =>
        Path.Combine(FileSystem.AppDataDirectory, "web_recent_history.json");
    private const string CloudflareChoiceTranslateBrowser = "translate_browser";
    private const string CloudflareChoiceCopyUrl = "copy_url";
    private const string CloudflareChoiceOpenBrowser = "open_browser";

    // ── Navigation Bar Optimization ───────────────────────────────────────────
    // Throttling to prevent rapid-fire UI updates during navigation
    private DateTime _lastNavBarUpdate = DateTime.MinValue;
    private const int NavBarUpdateThrottleMs = 100;
    private string? _pendingNavBarUpdateUrl;
    private bool _isNavBarUpdateScheduled;
    private readonly object _navBarUpdateLock = new();
    
    // Cache for site detection results to avoid redundant regex operations
    private string? _cachedSiteDetectionUrl;
    private string? _cachedSiteDetectionResult;
    private bool? _cachedNovelPageResult;

    // ── Back Navigation ───────────────────────────────────────────────────────
    // Tracks whether the last navigation was a GoBack() call so blank history
    // entries (about:blank, redirects) can be automatically skipped.
    private bool _isNavigatingBack;
    private int _backSkipCount;
    private const int MaxBackSkipAttempts = 10;
    private bool _webViewCleanedUp;

    // ── Book Info Panel ───────────────────────────────────────────────────────
    private bool _isBookInfoPanelOpen;
    private string? _bookInfoPanelUrl;
    private CancellationTokenSource? _bookInfoCts;

    // Simple cache so returning to a visited URL skips the re-fetch
    private record BookInfoCache(string Title, string TranslatedTitle,
        string Author, string TranslatedAuthor);
    private readonly Dictionary<string, BookInfoCache> _bookInfoCache =
        new(StringComparer.OrdinalIgnoreCase);

    private bool IsTranslateActive => _translateMode != WebTranslateMode.None;

    /// <summary>Returns true if this page is still present on the Shell stack (not popped).</summary>
    private bool IsStillOnNavigationStack()
    {
        try
        {
            return Shell.Current?.Navigation?.NavigationStack?.Contains(this) == true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns true when the URL represents an empty/blank browser page that should
    /// never be shown to the user during back navigation.
    /// </summary>
    private static bool IsBlankUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return true;

        if (url.Equals("about:blank", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
            url.Equals("chrome-error://chromewebdata/", StringComparison.OrdinalIgnoreCase))
            return true;

        // Inline frames / empty documents often appear during multi-step back.
        if (url.StartsWith("about:srcdoc", StringComparison.OrdinalIgnoreCase))
            return true;

        var u = url.Trim();
        // Tiny inline HTML placeholders sometimes sit in WebView history during redirects/back.
        if (u.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase) && u.Length < 128)
            return true;

        if (u.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// Set this before pushing WebBrowsePage. When the user taps Fetch,
    /// the URL is passed here and the WebView is popped so the caller
    /// can pre-fill its URL entry.
    /// </summary>
    public static Action<string>? OnUrlFetched { get; set; }

    public WebBrowsePage(string startUrl)
    {
        try
        {
            // Assign unique instance ID
            _instanceId = System.Threading.Interlocked.Increment(ref _instanceCounter);
            System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] Creating instance #{_instanceId}");

            // Try to clear any existing NameScope first
            try
            {
                var existingScope = Microsoft.Maui.Controls.Internals.NameScope.GetNameScope(this);
                if (existingScope != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] Found existing NameScope, clearing it");
                    Microsoft.Maui.Controls.Internals.NameScope.SetNameScope(this, null);
                }
            }
            catch { /* ignore */ }

            // Create a completely new NameScope for this instance
            var nameScope = new Microsoft.Maui.Controls.Internals.NameScope();
            Microsoft.Maui.Controls.Internals.NameScope.SetNameScope(this, nameScope);

            try
            {
                InitializeComponent();
            }
            catch (ArgumentException ex) when (ex.Message.Contains("already exists in this NameScope"))
            {
                // MAUI bug: NameScope conflict. Try to recover by forcing a new NameScope
                System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] NameScope conflict detected, attempting recovery");

                // Force clear and retry
                Microsoft.Maui.Controls.Internals.NameScope.SetNameScope(this, null);
                System.GC.Collect();
                System.GC.WaitForPendingFinalizers();

                var newScope = new Microsoft.Maui.Controls.Internals.NameScope();
                Microsoft.Maui.Controls.Internals.NameScope.SetNameScope(this, newScope);

                // Retry InitializeComponent
                InitializeComponent();
            }

            // Validate startUrl before proceeding
            if (string.IsNullOrWhiteSpace(startUrl))
            {
                startUrl = "https://www.google.com";
                System.Diagnostics.Debug.WriteLine("[WebBrowsePage] Warning: Empty startUrl, using fallback");
            }

            _currentUrl = startUrl;
            _actualWebNavigationUrl = startUrl;
            _homeUrl = startUrl;
            
            // Set URL immediately so navigation bar shows it instantly
            // This makes the nav bar appear fully loaded before WebView starts
            UrlBarLabel.Text = startUrl;
            
            _recentVisits = LoadRecentVisits();
            AddTab(startUrl, switchToTab: true);

            // Subscribe to WebView error events
            SiteWebView.Navigating += OnNavigating!;
            SiteWebView.Navigated += OnNavigated!;
            // Defer native WebView setup until PlatformView exists (image long-press, etc.)
            SiteWebView.HandlerChanged += OnWebViewHandlerChanged;

            // Initialize ad blocker icon state
            UpdateAdBlockerIcon();

            // Defer WebView navigation slightly to let UI render first
            // This makes the navigation bar appear instantly responsive
            _ = Task.Run(async () =>
            {
                await Task.Delay(50); // Small delay to let UI render
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    Navigate(startUrl);
                });
            });

            // Safety timeout: hide loading indicators after 15 seconds if still visible
            _ = Task.Run(async () =>
            {
                await Task.Delay(15000);
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    if (InitialLoadingOverlay?.IsVisible == true)
                    {
                        InitialLoadingOverlay.IsVisible = false;
                        InitialLoadingSpinner.IsRunning = false;
                        System.Diagnostics.Debug.WriteLine("[WebBrowsePage] Initial loading overlay hidden by safety timeout");
                    }
                    // Also hide URL bar loading indicator if stuck
                    if (UrlLoadingIndicator?.IsVisible == true)
                    {
                        UrlLoadingIndicator.IsVisible = false;
                        UrlLoadingIndicator.IsRunning = false;
                        System.Diagnostics.Debug.WriteLine("[WebBrowsePage] URL loading indicator hidden by safety timeout");
                    }
                });
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] Constructor error: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] Stack trace: {ex.StackTrace}");

            // Log to crash file
            try
            {
                var logPath = Path.Combine(FileSystem.CacheDirectory, "crash.log");
                var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] WebBrowsePage constructor: {ex.Message}\n{ex.StackTrace}\n\n";
                File.AppendAllText(logPath, logEntry);
            }
            catch { /* ignore logging errors */ }

            throw; // Re-throw to show error to user
        }
    }

    /// <summary>
    /// Intercepts the hardware/gesture back button. When the WebView has history,
    /// navigate back within the WebView instead of popping the page from the
    /// MAUI navigation stack.
    /// </summary>
    protected override bool OnBackButtonPressed()
    {
        // If any overlay/bottom-sheet is open, let it close first via its own logic
        if (_isBrowserMenuOpen || _isTabOverviewOpen || _isRecentLinksOpen ||
            _isImageContextMenuOpen || _isCloudflareSheetOpen)
            return base.OnBackButtonPressed();

        if (_webViewCleanedUp)
            return false;

        if (SiteWebView.CanGoBack)
        {
            _isNavigatingBack = true;
            _backSkipCount = 0;
            SiteWebView.GoBack();
            return true; // consumed — do not pop the page
        }

        return false; // let MAUI pop the page normally
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // Hide the persistent tab bar — it doesn't belong on the WebView page
        MainActivity.Instance?.SetTabBarVisible(false);
        UpdateBottomSheetMargins();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] Instance #{_instanceId} disappearing");

        // Restore the tab bar when leaving to a page that needs it
        var navStack = Navigation?.NavigationStack ?? Shell.Current?.Navigation?.NavigationStack;
        bool isPopped = Navigation == null || !Navigation.NavigationStack.Contains(this);
        if (isPopped)
        {
            var previousPage = navStack?.LastOrDefault(p => p != this);
            if (previousPage == null || 
                (previousPage is not AboutPage &&
                 previousPage is not SourceBrowsePage &&
                 previousPage is not WebBrowsePage &&
                 previousPage is not ShukaQuestPage))
            {
                MainActivity.Instance?.SetTabBarVisible(true);
            }
        }

        // Dispose only when this page is no longer on the Shell stack. Do not dispose on a
        // transient disappear while still pushed (avoids white screen when returning).
        if (!IsStillOnNavigationStack())
            CleanupWebView();
        else
        {
            // Pop often completes one frame after OnDisappearing — re-check so we still tear down.
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    if (!_webViewCleanedUp && !IsStillOnNavigationStack())
                        CleanupWebView();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] Deferred stack cleanup check: {ex.Message}");
                }
            });
        }
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        UpdateBottomSheetMargins();
    }

    /// <summary>
    /// Called when the page is being removed from the navigation stack.
    /// Ensures complete cleanup of resources.
    /// </summary>
    protected override void OnNavigatedFrom(NavigatedFromEventArgs args)
    {
        base.OnNavigatedFrom(args);

        if (!IsStillOnNavigationStack())
            CleanupWebView();
    }

    /// <summary>
    /// Properly disposes the WebView to prevent memory leaks.
    /// WebViews can hold significant memory and native resources.
    /// </summary>
    private void CleanupWebView()
    {
        if (_webViewCleanedUp)
            return;
        _webViewCleanedUp = true;

        try
        {
            if (SiteWebView != null)
            {
                // Unsubscribe from events to prevent memory leaks
                SiteWebView.Navigating -= OnNavigating!;
                SiteWebView.Navigated -= OnNavigated!;
                SiteWebView.HandlerChanged -= OnWebViewHandlerChanged;

                // Stop any ongoing navigation
                try
                {
                    // Clear the WebView source to stop loading
                    SiteWebView.Source = null;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] Error clearing WebView source: {ex.Message}");
                }

#if ANDROID
                // Android-specific cleanup
                if (SiteWebView.Handler?.PlatformView is global::Android.Webkit.WebView androidWebView)
                {
                    try
                    {
                        // Stop loading any content
                        androidWebView.StopLoading();

                        // Clear cache and history
                        androidWebView.ClearCache(true);
                        androidWebView.ClearHistory();

                        // Remove all views to break circular references
                        androidWebView.RemoveAllViews();

                        // Destroy the WebView
                        androidWebView.Destroy();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] Error during Android WebView cleanup: {ex.Message}");
                    }
                }
#endif
            }

            // Clear the handler to help with cleanup
            try
            {
                if (SiteWebView?.Handler != null)
                {
                    SiteWebView.Handler.DisconnectHandler();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] Error disconnecting handler: {ex.Message}");
            }

            _longClickListenerAttached = false;

            // Clear NameScope once native WebView is gone (was previously run on every disappear).
            try
            {
                Microsoft.Maui.Controls.Internals.NameScope.SetNameScope(this, new Microsoft.Maui.Controls.Internals.NameScope());
                System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] NameScope reset for instance #{_instanceId}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] Error clearing NameScope: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] Error during WebView cleanup: {ex.Message}");
        }
    }

    /// <summary>
    /// When the WebView's native control is ready, attach image long-press handling (Discover browser).
    /// </summary>
    private void OnWebViewHandlerChanged(object? sender, EventArgs e)
    {
        if (_webViewCleanedUp)
            return;

        if (SiteWebView.Handler?.PlatformView == null)
        {
            _longClickListenerAttached = false;
            return;
        }

        ConfigureWebViewForImageHandling();
    }

    /// <summary>
    /// Configures WebView for image hit-testing and long-press → Image Options sheet (same behavior as Shuka Quest).
    /// </summary>
    private void ConfigureWebViewForImageHandling()
    {
        try
        {
            if (_webViewCleanedUp)
                return;

#if ANDROID
            if (SiteWebView.Handler?.PlatformView is not global::Android.Webkit.WebView androidWebView)
                return;

            var settings = androidWebView.Settings;
            settings.UserAgentString = "Mozilla/5.0 (Linux; Android 10; SM-G973F) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36";
            settings.DomStorageEnabled = true;
            settings.DatabaseEnabled = true;
            settings.JavaScriptEnabled = true;
            settings.AllowFileAccess = false;
            settings.AllowContentAccess = false;
            settings.MixedContentMode = global::Android.Webkit.MixedContentHandling.CompatibilityMode;
            settings.DefaultTextEncodingName = "UTF-8";
            settings.MinimumFontSize = 8;
            settings.MinimumLogicalFontSize = 8;
            settings.SetSupportZoom(true);
            settings.BuiltInZoomControls = true;
            settings.DisplayZoomControls = false;
            settings.TextZoom = 100;
            settings.UseWideViewPort = true;
            settings.LoadWithOverviewMode = true;
            settings.JavaScriptCanOpenWindowsAutomatically = false;
            settings.SetSupportMultipleWindows(false);

            if (_longClickListenerAttached) return;
            _longClickListenerAttached = true;

            androidWebView.SetOnLongClickListener(new AndroidViewLongClickListener(_view =>
            {
                try
                {
                    var hit = androidWebView.GetHitTestResult();
                    if (hit == null) return false;

                    string? extra = hit.Extra;

                    // Image wrapped in an anchor: show both image and link sections.
                    // The image src is in hit.Extra; we use JS to retrieve the anchor href.
                    if (hit.Type == global::Android.Webkit.HitTestResult.SrcImageAnchorType)
                    {
                        string? imageUrl = extra;
                        if (!string.IsNullOrWhiteSpace(imageUrl))
                            _ = ShowImageAndLinkContextMenuAsync(imageUrl, anchorWebView: androidWebView);
                        return true;
                    }

                    // Pure anchor / text link — show only link actions.
                    bool isLink =
                        hit.Type == global::Android.Webkit.HitTestResult.AnchorType ||
                        hit.Type == global::Android.Webkit.HitTestResult.SrcAnchorType;

                    if (isLink)
                    {
                        if (!string.IsNullOrWhiteSpace(extra))
                            _ = ShowLinkContextMenuAsync(extra);
                        return true;
                    }

                    // Pure image (no anchor wrapper) — show only image actions.
                    if (hit.Type == global::Android.Webkit.HitTestResult.ImageType)
                    {
                        if (!string.IsNullOrWhiteSpace(extra))
                            _ = ShowImageContextMenuAsync(extra);
                        return true;
                    }

                    return false;
                }
                catch
                {
                    return false;
                }
            }));
#endif
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] ConfigureWebViewForImageHandling error: {ex.Message}");
        }
    }

#if ANDROID
    private sealed class AndroidViewLongClickListener : Java.Lang.Object, global::Android.Views.View.IOnLongClickListener
    {
        private readonly Func<global::Android.Views.View?, bool> _handler;

        public AndroidViewLongClickListener(Func<global::Android.Views.View?, bool> handler)
        {
            _handler = handler;
        }

        public bool OnLongClick(global::Android.Views.View? v)
        {
            try
            {
                return _handler(v);
            }
            catch
            {
                return false;
            }
        }
    }
#endif

    private async Task ShowImageContextMenuAsync(string? imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
            return;

        _currentImageContextMenuUrl = imageUrl;
        await ShowImageContextMenuSheetAsync(imageUrl);
    }

    private async Task ShowImageContextMenuSheetAsync(string imageUrl)
    {
        if (_isImageContextMenuOpen)
            return;

        _isImageContextMenuOpen = true;

        // Configure sections: image-only mode
        ImageContextMenuTitleLabel.Text = "Image Options";
        ImageContextMenuUrlLabel.Text = imageUrl;
        ImageOptionsSection.IsVisible = true;
        LinkOptionsSection.IsVisible = false;

        ImageContextMenuOverlay.IsVisible = true;
        ImageContextMenuOverlay.Opacity = 0;
        ImageContextMenuSheet.Opacity = 0;
        ImageContextMenuSheet.TranslationY = 30;

        await Task.WhenAll(
            ImageContextMenuOverlay.FadeToAsync(1, 160, Easing.CubicOut),
            ImageContextMenuSheet.FadeToAsync(1, 180, Easing.CubicOut),
            ImageContextMenuSheet.TranslateToAsync(0, 0, 180, Easing.CubicOut));
    }

    /// <summary>
    /// Shows the shared bottom sheet with only the Link Options section visible.
    /// Used when the long-pressed element is a plain text hyperlink.
    /// </summary>
    private async Task ShowLinkContextMenuAsync(string linkUrl)
    {
        if (string.IsNullOrWhiteSpace(linkUrl) || _isImageContextMenuOpen)
            return;

        _currentLinkContextMenuUrl = linkUrl;
        _currentImageContextMenuUrl = null;
        _isImageContextMenuOpen = true;

        // Configure sections: link-only mode
        ImageContextMenuTitleLabel.Text = "Link Options";
        ImageContextMenuUrlLabel.Text = linkUrl;
        ImageOptionsSection.IsVisible = false;
        LinkOptionsSection.IsVisible = true;
        LinkSectionHeaderLabel.IsVisible = false; // no divider needed in link-only mode

        ImageContextMenuOverlay.IsVisible = true;
        ImageContextMenuOverlay.Opacity = 0;
        ImageContextMenuSheet.Opacity = 0;
        ImageContextMenuSheet.TranslationY = 30;

        await Task.WhenAll(
            ImageContextMenuOverlay.FadeToAsync(1, 160, Easing.CubicOut),
            ImageContextMenuSheet.FadeToAsync(1, 180, Easing.CubicOut),
            ImageContextMenuSheet.TranslateToAsync(0, 0, 180, Easing.CubicOut));
    }

    /// <summary>
    /// Shows the shared bottom sheet with BOTH Image and Link sections when an image
    /// is wrapped inside an anchor tag. Uses JS to extract the anchor href accurately.
    /// </summary>
    private async Task ShowImageAndLinkContextMenuAsync(string imageUrl,
        global::Android.Webkit.WebView? anchorWebView = null)
    {
        if (string.IsNullOrWhiteSpace(imageUrl) || _isImageContextMenuOpen)
            return;

        _currentImageContextMenuUrl = imageUrl;

        // Try to fetch the anchor href via JS for accurate link extraction
        string? linkUrl = null;
        if (anchorWebView != null)
        {
            try
            {
                var jsResult = await SiteWebView.EvaluateJavaScriptAsync(
                    "(function(){" +
                    "  var el = document.elementFromPoint(" +
                    "    window.__shukaLongTapX || 0, window.__shukaLongTapY || 0);" +
                    "  while(el && el.tagName !== 'A') el = el.parentElement;" +
                    "  return el ? el.href : null;" +
                    "})()");
                if (!string.IsNullOrWhiteSpace(jsResult) && jsResult != "null")
                    linkUrl = jsResult.Trim('"');
            }
            catch { /* JS failed — treat as image-only */ }
        }

        _currentLinkContextMenuUrl = linkUrl;
        _isImageContextMenuOpen = true;

        bool hasLink = !string.IsNullOrWhiteSpace(linkUrl);

        ImageContextMenuTitleLabel.Text = hasLink ? "Image & Link Options" : "Image Options";
        ImageContextMenuUrlLabel.Text = imageUrl;
        ImageOptionsSection.IsVisible = true;
        LinkOptionsSection.IsVisible = hasLink;
        LinkSectionHeaderLabel.IsVisible = hasLink; // show "Link Options" divider when both sections present

        ImageContextMenuOverlay.IsVisible = true;
        ImageContextMenuOverlay.Opacity = 0;
        ImageContextMenuSheet.Opacity = 0;
        ImageContextMenuSheet.TranslationY = 30;

        await Task.WhenAll(
            ImageContextMenuOverlay.FadeToAsync(1, 160, Easing.CubicOut),
            ImageContextMenuSheet.FadeToAsync(1, 180, Easing.CubicOut),
            ImageContextMenuSheet.TranslateToAsync(0, 0, 180, Easing.CubicOut));
    }

    private async void OnLinkContextMenuOpenNewTabTapped(object sender, TappedEventArgs e)
    {
        var url = _currentLinkContextMenuUrl;
        await HideImageContextMenuSheetAsync();
        if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            AddTab(url, switchToTab: true);
            Navigate(url);
            await ShowQueuedToastAsync("Opened in new tab");
        }
    }

    private async void OnLinkContextMenuCopyUrlTapped(object sender, TappedEventArgs e)
    {
        var url = _currentLinkContextMenuUrl;
        await HideImageContextMenuSheetAsync();
        if (!string.IsNullOrWhiteSpace(url))
        {
            await Clipboard.Default.SetTextAsync(url);
            await ShowQueuedToastAsync("Link copied!");
        }
    }

    private async Task HideImageContextMenuSheetAsync()
    {
        if (!_isImageContextMenuOpen)
            return;

        _isImageContextMenuOpen = false;
        await Task.WhenAll(
            ImageContextMenuSheet.FadeToAsync(0, 140, Easing.CubicIn),
            ImageContextMenuSheet.TranslateToAsync(0, 24, 140, Easing.CubicIn),
            ImageContextMenuOverlay.FadeToAsync(0, 140, Easing.CubicIn));
        ImageContextMenuOverlay.IsVisible = false;
    }

    private async void OnImageContextMenuOverlayTapped(object sender, TappedEventArgs e)
        => await HideImageContextMenuSheetAsync();

    private void OnImageContextMenuSheetTapped(object sender, TappedEventArgs e) { }

    private async void OnImageContextMenuCloseTapped(object sender, TappedEventArgs e)
        => await HideImageContextMenuSheetAsync();

    private async void OnImageContextMenuOpenNewTabTapped(object sender, TappedEventArgs e)
    {
        var url = _currentImageContextMenuUrl;
        await HideImageContextMenuSheetAsync();
        if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            AddTab(url, switchToTab: true);
            Navigate(url);
            await ShowQueuedToastAsync("Opened in new tab");
        }
    }

    private async void OnImageContextMenuCopyImageTapped(object sender, TappedEventArgs e)
    {
        var url = _currentImageContextMenuUrl;
        await HideImageContextMenuSheetAsync();
        if (!string.IsNullOrWhiteSpace(url))
            await CopyImageToClipboardAsync(url);
    }

    private async void OnImageContextMenuCopyUrlTapped(object sender, TappedEventArgs e)
    {
        var url = _currentImageContextMenuUrl;
        await HideImageContextMenuSheetAsync();
        if (!string.IsNullOrWhiteSpace(url))
        {
            await Clipboard.Default.SetTextAsync(url);
            await ShowQueuedToastAsync("Image URL copied!");
        }
    }

    private async Task CopyImageToClipboardAsync(string imageUrl)
    {
        try
        {
            await ShowQueuedToastAsync("Downloading image…");

            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(20);
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Linux; Android 10) AppleWebKit/537.36 Chrome/120 Mobile Safari/537.36");

            var bytes = await httpClient.GetByteArrayAsync(imageUrl);

            string ext = ".jpg";
            var lowerUrl = imageUrl.ToLowerInvariant();
            if (lowerUrl.Contains(".png")) ext = ".png";
            else if (lowerUrl.Contains(".gif")) ext = ".gif";
            else if (lowerUrl.Contains(".webp")) ext = ".webp";

            var cachePath = Path.Combine(FileSystem.CacheDirectory, $"shuka_webbrowse_img_copy{ext}");
            await File.WriteAllBytesAsync(cachePath, bytes);

#if ANDROID
            var ctx = global::Android.App.Application.Context;
            var file = new Java.IO.File(cachePath);
            var uri = global::AndroidX.Core.Content.FileProvider.GetUriForFile(
                ctx, "com.seizue.shuka.fileprovider", file);

            var clip = global::Android.Content.ClipData.NewUri(
                ctx.ContentResolver, "Copied Image", uri);
            var clipboard = (global::Android.Content.ClipboardManager)
                ctx.GetSystemService(global::Android.Content.Context.ClipboardService)!;
            clipboard.PrimaryClip = clip;
#endif
            await ShowQueuedToastAsync("Image copied!");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] CopyImageToClipboardAsync error: {ex.Message}");
            try
            {
                await Clipboard.Default.SetTextAsync(imageUrl);
                await ShowQueuedToastAsync("Copied image URL (image download failed)");
            }
            catch { /* ignore */ }
        }
    }

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();

        // If handler is being removed, clean up
        if (Handler == null)
        {
            System.Diagnostics.Debug.WriteLine("[WebBrowsePage] Handler removed, cleaning up");
        }
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    /// <summary>
    /// Navigates to the specified URL with validation and error handling.
    /// </summary>
    private void Navigate(string url)
    {
        try
        {
            if (_webViewCleanedUp)
                return;

            // Validate URL format
            if (string.IsNullOrWhiteSpace(url))
            {
                ShowNavigationError("Invalid URL", "The URL cannot be empty.");
                return;
            }

            // Ensure URL has a scheme
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://" + url;
            }

            // Validate URI format
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            {
                ShowNavigationError("Invalid URL", $"The URL format is invalid:\n{url}");
                return;
            }

            // Check for valid scheme
            if (uri.Scheme != "http" && uri.Scheme != "https")
            {
                ShowNavigationError("Unsupported Protocol", $"Only HTTP and HTTPS URLs are supported.\n{url}");
                return;
            }

            _currentUrl = url;
            _actualWebNavigationUrl = url;
            UpdateActiveTab(url, null);

            // Update UI - use batched navigation bar update for better performance
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    SiteWebView.Source = new UrlWebViewSource { Url = url };
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] UI update error: {ex.Message}");
                    ShowNavigationError("Navigation Error", "Failed to load the page.");
                }
            });
            
            // Batched navigation bar update (throttled, background-threaded)
            UpdateNavigationBar(url);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] Navigate error: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] Stack trace: {ex.StackTrace}");
            ShowNavigationError("Navigation Error", $"Failed to navigate to URL:\n{ex.Message}");
        }
    }

    /// <summary>
    /// Shows an error banner when navigation fails.
    /// </summary>
    private async void ShowNavigationError(string title, string message)
    {
        await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            try
            {
                await DisplayAlertAsync(title, message, "OK");
            }
            catch
            {
                // Fallback: show in invalid URL banner if alert fails
                InvalidUrlHintLabel.Text = $"{title}: {message}";
                await ShowInvalidUrlBannerAsync(message);
            }
        });
    }

    private async void OnBackTapped(object sender, TappedEventArgs e)
    {
        try
        {
            if (_webViewCleanedUp)
            {
                if (Shell.Current?.Navigation != null)
                    await Shell.Current.Navigation.PopAsync();
                return;
            }

            if (SiteWebView.CanGoBack)
            {
                _isNavigatingBack = true;
                _backSkipCount = 0;
                SiteWebView.GoBack();
            }
            else
            {
                await Shell.Current.Navigation.PopAsync();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] Back navigation error: {ex.Message}");
        }
    }

    private void OnForwardTapped(object sender, TappedEventArgs e)
    {
        try
        {
            if (SiteWebView.CanGoForward)
            {
                SiteWebView.GoForward();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] Forward navigation error: {ex.Message}");
        }
    }

    private void OnReloadTapped(object sender, TappedEventArgs e)
    {
        try
        {
            SiteWebView.Reload();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] Reload error: {ex.Message}");
            ShowNavigationError("Reload Failed", "Could not reload the page. Please try again.");
        }
    }

    private void OnHomeSourceTapped(object sender, TappedEventArgs e)
    {
        // Reset translate state when going home
        if (IsTranslateActive)
        {
            _translateMode = WebTranslateMode.None;
            _originalUrl = string.Empty;
            UpdateTranslateFabAppearance();
        }
        Navigate(_homeUrl);
    }

    private async void OnTabsTapped(object sender, TappedEventArgs e)
    {
        try
        {
            await ShowTabOverviewAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] Tabs menu error: {ex.Message}");
        }
    }

    private async void OnHistoryTapped(object sender, TappedEventArgs e)
    {
        await ShowRecentHistoryAsync();
    }

    private async void OnAdBlockerToggleTapped(object? sender = null, TappedEventArgs? e = null)
    {
        // Toggle the ad blocker
        AdBlockerService.Instance.IsEnabled = !AdBlockerService.Instance.IsEnabled;
        UpdateAdBlockerIcon();

        // Show toast notification
        string message = AdBlockerService.Instance.IsEnabled
            ? "Ad Blocker: ON"
            : "Ad Blocker: OFF";
        await ShowQueuedToastAsync(message);

        // Full navigation reapplies native interception + injected filters (Reload alone can be cache-heavy).
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_currentUrl))
                    Navigate(_currentUrl);
                else
                    SiteWebView.Reload();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] Ad blocker toggle refresh: {ex.Message}");
            }
        });
    }

    private void UpdateAdBlockerIcon()
    {
        // Top bar icon removed; state is now surfaced in the More menu.
    }

    private async void OnOpenInBrowserTapped(object sender, TappedEventArgs e)
    {
        try { await Launcher.Default.OpenAsync(new Uri(_currentUrl)); }
        catch { /* ignore */ }
    }

    private async void OnMoreTapped(object sender, TappedEventArgs e)
    {
        await ShowBrowserMenuAsync();
    }

    // Sites that use Cloudflare — Google Translate proxy can't load them in WebView.
    // For these, we open the translated URL in the external browser instead.
    private static readonly HashSet<string> _cfSites = new(StringComparer.OrdinalIgnoreCase)
    {
        "69shuba.com", "czbooks.net"
    };

    private async void OnTranslateTapped(object sender, TappedEventArgs e)
    {
        try
        {
            await FabTranslate.ScaleToAsync(0.92, 70, Easing.CubicOut);
            await FabTranslate.ScaleToAsync(1.0, 70, Easing.SpringOut);

            if (IsTranslateActive)
            {
                // Exit Google’s translate frame: open the real site URL for what you’re reading now (not the menu you started from).
                if (_translateMode == WebTranslateMode.GoogleProxy)
                {
                    string? leaveUrl = SanitizeLeaveTranslateTarget(await PickBestLeaveTranslateUrlAsync().ConfigureAwait(true));

                    _translateMode = WebTranslateMode.None;
                    _originalUrl = string.Empty;
                    UpdateTranslateFabAppearance();
                    TranslateEmbeddedFrameTracker.Clear();

                    if (!string.IsNullOrWhiteSpace(leaveUrl))
                        await DontTranslateReloadReaderInWebViewAsync(leaveUrl).ConfigureAwait(true);
                    else
                        await ShowQueuedToastAsync("Could not leave translate. Try Back or reload the site.");
                }

                return;
            }

            if (string.IsNullOrWhiteSpace(_currentUrl))
            {
                await DisplayAlertAsync("Cannot Translate", "No page is currently loaded.", "OK");
                return;
            }

            string? site = DetectSite(_currentUrl);
            bool isCf = site != null && _cfSites.Contains(site);

            if (isCf)
            {
                string? choiceCf = await ShowCloudflareTranslateSheetAsync(site!);

                if (choiceCf == CloudflareChoiceTranslateBrowser)
                {
                    string encoded = Uri.EscapeDataString(_currentUrl);
                    string translateUrl = $"https://translate.google.com/translate?sl=auto&tl=en&u={encoded}";
                    try { await Launcher.Default.OpenAsync(new Uri(translateUrl)); }
                    catch (Exception ex)
                    {
                        await DisplayAlertAsync("Error", $"Could not open browser:\n{ex.Message}", "OK");
                    }
                }
                else if (choiceCf == CloudflareChoiceCopyUrl)
                {
                    await Clipboard.Default.SetTextAsync(_currentUrl);
                    await ShowQueuedToastAsync("URL copied to clipboard!");
                }
                else if (choiceCf == CloudflareChoiceOpenBrowser)
                {
                    try { await Launcher.Default.OpenAsync(new Uri(_currentUrl)); }
                    catch (Exception ex)
                    {
                        await DisplayAlertAsync("Error", $"Could not open browser:\n{ex.Message}", "OK");
                    }
                }

                return;
            }

            await StartGoogleProxyTranslateAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] Translate error: {ex.Message}");
            await DisplayAlertAsync("Translation Error",
                $"An error occurred while translating:\n{ex.Message}", "OK");
        }
    }

    private static bool IsGoogleTranslateWrapperPage(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        string host = uri.IdnHost ?? uri.Host;
        return host.Equals("translate.google.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".translate.google.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("translate.googleusercontent.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".translate.goog", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Mobile / WebView translate proxy: <c>https://www-52shuku-net.translate.goog/path?_x_tr_sl=...</c>
    /// → <c>https://www.52shuku.net/path</c> (host: hyphens → dots; strip <c>_x_tr_*</c> query params).
    /// </summary>
    /// <remarks>
    /// The decoded host matches reader domains in <c>Shuka.Core.Adapters</c> (e.g. <c>52shuku.net</c>, <c>dmxs.org</c>,
    /// <c>czbooks.net</c>) — same sites as <see cref="BookService"/>, but we avoid adapter <c>NormalizeUrl</c> here
    /// because it rewrites chapter URLs for downloading.
    /// </remarks>
    private static bool TryUnwrapTranslateGoogProxyUrl(string? url, out string originalUrl)
    {
        originalUrl = "";
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        var host = (uri.IdnHost ?? uri.Host).ToLowerInvariant();
        const string googSuffix = ".translate.goog";
        if (!host.EndsWith(googSuffix, StringComparison.OrdinalIgnoreCase))
            return false;

        string encodedHost = host[..^googSuffix.Length];
        if (string.IsNullOrEmpty(encodedHost))
            return false;

        string realHost = encodedHost.Replace('-', '.');
        if (string.IsNullOrEmpty(realHost))
            return false;

        var kept = new List<string>();
        string q = uri.Query;
        if (!string.IsNullOrEmpty(q) && q[0] == '?')
            q = q[1..];

        if (!string.IsNullOrEmpty(q))
        {
            foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                string name = eq >= 0 ? part[..eq] : part;
                if (name.StartsWith("_x_tr_", StringComparison.OrdinalIgnoreCase))
                    continue;
                kept.Add(part);
            }
        }

        try
        {
            var ub = new UriBuilder(uri)
            {
                Host = realHost,
            };

            ub.Query = kept.Count > 0 ? string.Join("&", kept) : string.Empty;

            originalUrl = ub.Uri.AbsoluteUri;
            return Uri.TryCreate(originalUrl, UriKind.Absolute, out var ok)
                && (ok.Scheme == Uri.UriSchemeHttp || ok.Scheme == Uri.UriSchemeHttps);
        }
        catch
        {
            return false;
        }
    }

    private async Task StartGoogleProxyTranslateAsync()
    {
        TranslateEmbeddedFrameTracker.Clear();

        string targetUrl = _currentUrl;
        if (IsGoogleTranslateWrapperPage(_currentUrl))
        {
            var acc = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await AddLeaveCandidatesFromTranslateDomAsync(acc).ConfigureAwait(true);
            AddLeaveCandidatesFromString(_currentUrl, acc);
            var best = PickLongestLeaveTarget(acc);
            if (!string.IsNullOrWhiteSpace(best))
                targetUrl = best;
        }

        if (string.IsNullOrWhiteSpace(targetUrl))
            return;

        string encoded = Uri.EscapeDataString(targetUrl);
        string translateUrl = $"https://translate.google.com/translate?sl=auto&tl=en&u={encoded}";

        _originalUrl = targetUrl;
        _translateMode = WebTranslateMode.GoogleProxy;
        UpdateTranslateFabAppearance();
        Navigate(translateUrl);
    }

    /// <summary>
    /// Collects every <c>u=</c> target and direct reader URL, then picks the <b>longest</b> plausible site URL
    /// so e.g. <c>/gl/09_b/bkdLu.html</c> wins over <c>/gl/</c> when both appear (52shuku + Google Translate).
    /// </summary>
    private async Task<string?> PickBestLeaveTranslateUrlAsync()
    {
        string? bar = GetAddressBarUrlForLeave();
        var acc = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (TranslateEmbeddedFrameTracker.TryGetLatest(out var tracked))
                AddLeaveCandidatesFromString(tracked, acc);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] PickBestLeaveTranslateUrl tracker: {ex.Message}");
        }

        try
        {
            await AddLeaveCandidatesFromTranslateDomAsync(acc).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] PickBestLeaveTranslateUrl dom: {ex.Message}");
        }

        string? topJsHref = null;
        try
        {
            string? topRaw = await SiteWebView.EvaluateJavaScriptAsync("window.location.href").ConfigureAwait(true);
            topJsHref = UnwrapJsResultJsonString(topRaw);
            AddLeaveCandidatesFromString(topJsHref, acc);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] PickBestLeaveTranslateUrl top href: {ex.Message}");
        }

        AddLeaveCandidatesFromString(bar, acc);
        AddLeaveCandidatesFromString(_currentUrl, acc);
        AddLeaveCandidatesFromString(UrlBarLabel.Text, acc);
        AddLeaveCandidatesFromString(_originalUrl, acc);

        var best = PickLongestLeaveTarget(acc);
        if (!string.IsNullOrWhiteSpace(best))
            return best;

        return FallbackDecodeFromTranslateWrapper(bar)
            ?? FallbackDecodeFromTranslateWrapper(_currentUrl)
            ?? FallbackDecodeFromTranslateWrapper(topJsHref)
            ?? (TranslateEmbeddedFrameTracker.IsPlausibleReaderSiteUrl(_originalUrl) ? _originalUrl : null)
            ?? (IsLeaveTargetRelaxed(_originalUrl) ? _originalUrl : null);
    }

    /// <summary>What the user sees in the URL bar (label can be fresher than <see cref="_currentUrl"/> on some navigations).</summary>
    private string? GetAddressBarUrlForLeave()
    {
        try
        {
            string? label = UrlBarLabel.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(label) &&
                (label.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 label.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                return label;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] GetAddressBarUrlForLeave label: {ex.Message}");
        }

        string? cur = _currentUrl?.Trim();
        if (!string.IsNullOrWhiteSpace(cur))
            return cur;

        return null;
    }

    private async Task AddLeaveCandidatesFromTranslateDomAsync(HashSet<string> acc)
    {
        const string collectRawJs = """
(function(){
  var out = [];
  function push(s) {
    if (!s) return;
    s = String(s).trim();
    if (!s) return;
    if (out.indexOf(s) < 0) out.push(s);
  }
  var ifs = document.querySelectorAll('iframe');
  for (var i = 0; i < ifs.length; i++) {
    var f = ifs[i];
    try {
      if (f.contentWindow && f.contentWindow.location && f.contentWindow.location.href)
        push(f.contentWindow.location.href);
    } catch (e) {}
    push(f.src || '');
  }
  if (window.location && window.location.href) push(window.location.href);
  return JSON.stringify({ raw: out });
})()
""";

        string? raw = await SiteWebView.EvaluateJavaScriptAsync(collectRawJs).ConfigureAwait(true);
        string? json = UnwrapJsResultJsonString(raw)?.Trim();
        if (string.IsNullOrEmpty(json))
            return;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("raw", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return;

            foreach (var el in arr.EnumerateArray())
            {
                string? s = el.GetString();
                AddLeaveCandidatesFromString(s, acc);
            }
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] AddLeaveCandidatesFromTranslateDom parse: {ex.Message}");
        }
    }

    /// <summary>Pulls every decoded <c>u=</c> chain plus the string itself if it is already a reader URL.</summary>
    private static void AddLeaveCandidatesFromString(string? blob, HashSet<string> acc, int depth = 0)
    {
        if (depth > 8 || string.IsNullOrWhiteSpace(blob))
            return;

        blob = blob.Trim();
        if (TryUnwrapTranslateGoogProxyUrl(blob, out var fromGoog))
        {
            acc.Add(fromGoog);
            AddLeaveCandidatesFromString(fromGoog, acc, depth + 1);
        }

        foreach (Match m in Regex.Matches(blob, @"[?&]u=([^&]*)", RegexOptions.IgnoreCase))
        {
            string enc = m.Groups[1].Value;
            if (string.IsNullOrEmpty(enc))
                continue;

            string? dec = TryDecodeTranslateUValueToHttpUrl(enc.Replace('+', ' '));
            if (string.IsNullOrWhiteSpace(dec))
                continue;

            acc.Add(dec);
            AddLeaveCandidatesFromString(dec, acc, depth + 1);
        }

        if (TranslateEmbeddedFrameTracker.IsPlausibleReaderSiteUrl(blob))
            acc.Add(blob);
    }

    private static string? PickLongestLeaveTarget(HashSet<string> acc)
    {
        IEnumerable<string> NonShell(IEnumerable<string> q) =>
            q.Where(s => !IsGoogleTranslateWrapperPage(s) && !IsGoogleSearchOrRedirectShell(s));

        var strict = NonShell(acc.Where(TranslateEmbeddedFrameTracker.IsPlausibleReaderSiteUrl))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var pool = strict.Count > 0
            ? strict
            : NonShell(acc.Where(IsLeaveTargetRelaxed)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (pool.Count == 0)
            return null;

        return pool
            .OrderByDescending(u => u.Length)
            .ThenByDescending(u => Uri.TryCreate(u, UriKind.Absolute, out var x) ? x.AbsolutePath.Length : 0)
            .First();
    }

    /// <summary>Never “leave” to another Google translate/search URL — that keeps the WebView in translate mode.</summary>
    private static bool IsGoogleSearchOrRedirectShell(string? u)
    {
        if (string.IsNullOrWhiteSpace(u) || !Uri.TryCreate(u.Trim(), UriKind.Absolute, out var x))
            return false;

        var h = (x.IdnHost ?? x.Host).ToLowerInvariant();
        if (h is "google.com" or "www.google.com")
            return true;
        if (h.EndsWith(".googleusercontent.com", StringComparison.Ordinal))
            return true;
        if (h.EndsWith(".gstatic.com", StringComparison.Ordinal))
            return true;

        return false;
    }

    /// <summary>Unwrap nested translate wrappers until we have a normal site URL (or null).</summary>
    private static string? SanitizeLeaveTranslateTarget(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        url = url.Trim();
        if (TryUnwrapTranslateGoogProxyUrl(url, out var googUnwrapped))
            url = googUnwrapped;

        for (var i = 0; i < 6 && (IsGoogleTranslateWrapperPage(url) || IsGoogleSearchOrRedirectShell(url)); i++)
        {
            string? next = FallbackDecodeFromTranslateWrapper(url);
            if (string.IsNullOrWhiteSpace(next) && TryExtractLastEmbeddedUParameter(url, out var one))
                next = one;
            if (string.IsNullOrWhiteSpace(next) || string.Equals(next.Trim(), url, StringComparison.OrdinalIgnoreCase))
                return null;
            url = next.Trim();
        }

        if (IsGoogleTranslateWrapperPage(url) || IsGoogleSearchOrRedirectShell(url))
            return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
            return null;

        return BookService.EnsureHttpsIfKnownReaderSite(url);
    }

    private void HideLeaveTranslateOverlay()
    {
        LeaveOriginalSpinner.IsRunning = false;
        LeaveOriginalOverlay.IsVisible = false;
        LeaveOriginalSubtitle.Text = "";
    }

    /// <summary>Loads the sanitized reader URL in the WebView only (no external browser).</summary>
    private async Task DontTranslateReloadReaderInWebViewAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        url = BookService.EnsureHttpsIfKnownReaderSite(url.Trim());

        HideLeaveTranslateOverlay();

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            try
            {
                LoadingBar.IsVisible = true;
                LoadingBar.Progress = 0;
                _ = AnimateLoadingBarAsync();

#if ANDROID
                if (SiteWebView.Handler?.PlatformView is global::Android.Webkit.WebView wv)
                {
                    try
                    {
                        wv.StopLoading();
                        wv.LoadUrl(url);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] DontTranslate ReloadWeb LoadUrl: {ex.Message}");
                    }
                }
#endif
                SiteWebView.Source = new UrlWebViewSource { Url = url };
                _currentUrl = url;
                _actualWebNavigationUrl = url;
                
                // Batched navigation bar update (throttled, background-threaded)
                UpdateNavigationBar(url);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] DontTranslateReload UI: {ex.Message}");
                ShowNavigationError("Navigation Error", "Could not reload reader.");
            }
        }).ConfigureAwait(true);

        await ShowQueuedToastAsync("Left translate — showing the original page here.");
    }

    /// <summary>Http(s) target that is not Google’s translate hostname (looser than <see cref="TranslateEmbeddedFrameTracker.IsPlausibleReaderSiteUrl"/>).</summary>
    private static bool IsLeaveTargetRelaxed(string? u)
    {
        if (string.IsNullOrWhiteSpace(u))
            return false;
        if (!Uri.TryCreate(u.Trim(), UriKind.Absolute, out var x))
            return false;
        if (x.Scheme != Uri.UriSchemeHttp && x.Scheme != Uri.UriSchemeHttps)
            return false;

        var h = (x.IdnHost ?? x.Host).ToLowerInvariant();
        if (h.Contains("translate.google", StringComparison.Ordinal))
            return false;
        if (h is "translate.googleusercontent.com")
            return false;
        if (IsGoogleSearchOrRedirectShell(u))
            return false;

        return true;
    }

    /// <summary>Decode every <c>u=</c> from a translate wrapper URL and pick the longest usable target.</summary>
    private static string? FallbackDecodeFromTranslateWrapper(string? wrapperUrl)
    {
        if (string.IsNullOrWhiteSpace(wrapperUrl))
            return null;

        var decoded = new List<string>();
        foreach (Match m in Regex.Matches(wrapperUrl.Trim(), @"[?&]u=([^&]*)", RegexOptions.IgnoreCase))
        {
            string? d = TryDecodeTranslateUValueToHttpUrl(m.Groups[1].Value.Replace('+', ' '));
            if (!string.IsNullOrWhiteSpace(d))
                decoded.Add(d!);
        }

        if (decoded.Count == 0)
            return null;

        var strict = decoded.Where(TranslateEmbeddedFrameTracker.IsPlausibleReaderSiteUrl).ToList();
        var pool = strict.Count > 0 ? strict : decoded.Where(IsLeaveTargetRelaxed).ToList();
        if (pool.Count == 0)
            return null;

        var picked = pool
            .OrderByDescending(u => u.Length)
            .ThenByDescending(u => Uri.TryCreate(u, UriKind.Absolute, out var x) ? x.AbsolutePath.Length : 0)
            .First();

        return IsGoogleTranslateWrapperPage(picked) || IsGoogleSearchOrRedirectShell(picked) ? null : picked;
    }

    private void ScheduleTranslateEmbeddedOriginalSync()
    {
        int gen = System.Threading.Interlocked.Increment(ref _translateEmbeddedSyncGeneration);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(450).ConfigureAwait(false);
                if (gen != _translateEmbeddedSyncGeneration)
                    return;

                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    try
                    {
                        if (_translateMode != WebTranslateMode.GoogleProxy)
                            return;
                        var acc = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        if (TranslateEmbeddedFrameTracker.TryGetLatest(out var t))
                            AddLeaveCandidatesFromString(t, acc);
                        await AddLeaveCandidatesFromTranslateDomAsync(acc).ConfigureAwait(true);
                        AddLeaveCandidatesFromString(_currentUrl, acc);
                        var best = PickLongestLeaveTarget(acc);
                        if (!string.IsNullOrWhiteSpace(best))
                            _originalUrl = best;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] ScheduleTranslateEmbeddedOriginalSync: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] ScheduleTranslateEmbeddedOriginalSync outer: {ex.Message}");
            }
        });
    }

    /// <summary>Last <c>u=</c> query segment decodes to the embedded http(s) URL (Translate top URL or iframe <c>src</c>).</summary>
    private static bool TryExtractLastEmbeddedUParameter(string? url, out string embeddedUrl)
    {
        embeddedUrl = "";
        if (string.IsNullOrWhiteSpace(url))
            return false;

        MatchCollection matches = Regex.Matches(url, @"[?&]u=([^&]*)", RegexOptions.IgnoreCase);
        if (matches.Count == 0)
            return false;

        string encVal = matches[matches.Count - 1].Groups[1].Value;
        if (string.IsNullOrEmpty(encVal))
            return false;

        string? decoded = TryDecodeTranslateUValueToHttpUrl(encVal.Replace('+', ' '));
        if (string.IsNullOrWhiteSpace(decoded))
            return false;

        embeddedUrl = decoded;
        return true;
    }

    private static string? TryDecodeTranslateUValueToHttpUrl(string value)
    {
        string s = value;
        for (var pass = 0; pass < 4; pass++)
        {
            if (Uri.TryCreate(s, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                return uri.ToString();

            try
            {
                string next = Uri.UnescapeDataString(s);
                if (next == s)
                    return null;
                s = next;
            }
            catch (UriFormatException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>MAUI/Android wraps JS results as a JSON string literal — unwrap one level.</summary>
    private static string? UnwrapJsResultJsonString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        raw = raw.Trim();
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            try { return JsonSerializer.Deserialize<string>(raw); }
            catch (JsonException) { /* ignore */ }
        }

        return raw;
    }

    /// <summary>
    /// Updates the Translate FAB to show active/inactive state.
    /// Active = Google Translate session (accent + DON'T TRANSLATE — leaves wrapper to the current site URL).
    /// </summary>
    private void UpdateTranslateFabAppearance()
    {
        if (IsTranslateActive)
        {
            FabTranslate.SetDynamicResource(Border.BackgroundColorProperty, "AccentContainer");
            FabTranslate.SetDynamicResource(Border.StrokeProperty, "AccentLight");
            FabTranslateIcon.SetDynamicResource(Label.TextColorProperty, "AccentLight");
            FabTranslateLabel.SetDynamicResource(Label.TextColorProperty, "AccentLight");
            FabTranslateLabel.Text = "DON'T TRANSLATE";
        }
        else
        {
            FabTranslate.SetDynamicResource(Border.BackgroundColorProperty, "BgCard");
            FabTranslate.SetDynamicResource(Border.StrokeProperty, "Stroke");
            FabTranslateIcon.SetDynamicResource(Label.TextColorProperty, "TextSecondary");
            FabTranslateLabel.SetDynamicResource(Label.TextColorProperty, "TextSecondary");
            FabTranslateLabel.Text = "TRANSLATE";
        }
    }

    // ── WebView events ────────────────────────────────────────────────────────

    private void OnNavigating(object sender, WebNavigatingEventArgs e)
    {
        try
        {
            if (_webViewCleanedUp)
                return;

            // Collapse FAB menu when navigating
            if (_fabMenuExpanded)
            {
                _fabMenuExpanded = false;
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    _ = Task.WhenAll(
                        FabToggleIcon.RotateToAsync(0, 250, Easing.CubicOut),
                        FabMenuItems.FadeToAsync(0, 200, Easing.CubicIn)
                    );
                    FabMenuItems.IsVisible = false;
                });
            }

            _isLoading = true;
            LoadingBar.IsVisible = true;
            LoadingBar.Progress = 0;
            _ = AnimateLoadingBarAsync();
            
            // Show URL bar loading indicator immediately for responsive feel
            UrlLoadingIndicator.IsVisible = true;
            UrlLoadingIndicator.IsRunning = true;

            _actualWebNavigationUrl = e.Url ?? string.Empty;
            _currentUrl = e.Url ?? string.Empty;
            UpdateActiveTab(_currentUrl, null);

            // Batched navigation bar update (throttled, background-threaded)
            UpdateNavigationBar(_currentUrl);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] OnNavigating error: {ex.Message}");
            e.Cancel = true;
            ShowNavigationError("Navigation Error", "An error occurred while navigating.");
        }
    }

    private async void OnNavigated(object sender, WebNavigatedEventArgs e)
    {
        try
        {
            if (_webViewCleanedUp)
                return;

            _isLoading = false;
            LoadingBar.IsVisible = false;
            LoadingBar.Progress = 0;
            
            // Hide URL bar loading indicator
            UrlLoadingIndicator.IsVisible = false;
            UrlLoadingIndicator.IsRunning = false;

            // Hide the initial loading overlay as soon as the page loads
            if (InitialLoadingOverlay.IsVisible)
            {
                InitialLoadingOverlay.IsVisible = false;
                InitialLoadingSpinner.IsRunning = false;
            }

            // Check navigation result
            if (e.Result == WebNavigationResult.Failure)
            {
                ShowNavigationError("Page Load Failed",
                    "The page could not be loaded. Please check your internet connection and try again.");
                return;
            }
            else if (e.Result == WebNavigationResult.Timeout)
            {
                ShowNavigationError("Connection Timeout",
                    "The page took too long to load. Please try again.");
                return;
            }

            _actualWebNavigationUrl = e.Url ?? string.Empty;
            _currentUrl = e.Url ?? string.Empty;

            // Auto-skip blank/empty history entries that occur during back navigation.
            // Intermediate redirect pages and about:blank entries cause the white-screen
            // bug when the user presses Back multiple times.
            if (_isNavigatingBack && IsBlankUrl(_currentUrl))
            {
                if (_backSkipCount < MaxBackSkipAttempts && SiteWebView.CanGoBack)
                {
                    _backSkipCount++;
                    System.Diagnostics.Debug.WriteLine(
                        $"[WebBrowsePage] Skipping blank back entry #{_backSkipCount}: '{_currentUrl}'");
                    // Defer GoBack so we are not inside WebView re-entrant navigation (reduces white flashes).
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        try
                        {
                            if (_webViewCleanedUp || SiteWebView == null) return;
                            if (SiteWebView.CanGoBack)
                                SiteWebView.GoBack();
                        }
                        catch (Exception goEx)
                        {
                            System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] Deferred GoBack: {goEx.Message}");
                        }
                    });
                    return;
                }
                else
                {
                    // Exhausted back history or skip limit — fall back to home URL
                    System.Diagnostics.Debug.WriteLine(
                        "[WebBrowsePage] Back skip limit reached or no more history; navigating home.");
                    _isNavigatingBack = false;
                    _backSkipCount = 0;
                    Navigate(_homeUrl);
                    return;
                }
            }

            // Real page loaded — reset back-navigation flags
            _isNavigatingBack = false;
            _backSkipCount = 0;

            string? title = null;
            try
            {
                title = UnwrapJsResultJsonString(
                    await SiteWebView.EvaluateJavaScriptAsync("document.title").ConfigureAwait(true));
            }
            catch { }
            UpdateActiveTab(_currentUrl, title);
            AddRecentVisit(_currentUrl, title);

            if (_translateMode == WebTranslateMode.GoogleProxy)
                ScheduleTranslateEmbeddedOriginalSync();

            // Batched navigation bar update (throttled, background-threaded)
            UpdateNavigationBar(_currentUrl);

            // Inject ad blocker cosmetic filter from MAUI layer as well,
            // since OnPageFinished in the native handler may fire before ad scripts run.
            _ = InjectAdBlockerAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] OnNavigated error: {ex.Message}");
            _isLoading = false;
            LoadingBar.IsVisible = false;
            
            // Hide URL bar loading indicator on error
            UrlLoadingIndicator.IsVisible = false;
            UrlLoadingIndicator.IsRunning = false;

            // Hide initial loading overlay on error
            InitialLoadingOverlay.IsVisible = false;
            InitialLoadingSpinner.IsRunning = false;
        }
    }

    /// <summary>
    /// Injects the ad blocker script immediately and with multiple delayed passes
    /// to catch ads that are injected by scripts after the page finishes loading.
    /// Uses an aggressive multi-pass approach like uBlock Origin.
    /// </summary>
    private async Task InjectAdBlockerAsync()
    {
        try
        {
            System.Diagnostics.Debug.WriteLine("[WebBrowsePage] ===== AD BLOCKER INJECTION START =====");

            // Inject touch coordinates tracker for long taps
            var coordsJs = @"
(function(){
  if (!window.__shukaCoordsRegistered) {
    window.__shukaCoordsRegistered = true;
    window.__shukaLongTapX = 0;
    window.__shukaLongTapY = 0;
    window.addEventListener('touchstart', function(e) {
      if (e.touches.length > 0) {
        window.__shukaLongTapX = e.touches[0].clientX;
        window.__shukaLongTapY = e.touches[0].clientY;
      }
    }, {passive: true});
    window.addEventListener('contextmenu', function(e) {
      window.__shukaLongTapX = e.clientX;
      window.__shukaLongTapY = e.clientY;
    });
  }
})();
";
            await SiteWebView.EvaluateJavaScriptAsync(coordsJs);

            var js = AdBlockerService.Instance.GetCosmeticFilterScript();
            if (string.IsNullOrEmpty(js))
            {
                System.Diagnostics.Debug.WriteLine("[WebBrowsePage] WARNING: Ad blocker script is empty!");
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] Ad blocker script length: {js.Length} chars");

            // First pass — run immediately
            await SiteWebView.EvaluateJavaScriptAsync(js);
            System.Diagnostics.Debug.WriteLine("[WebBrowsePage] ✓ Ad blocker pass 1 complete");

            // Check if it's working by inspecting the page
            await Task.Delay(500);
            var inspectJs = @"
(function(){
  var report = {
    iframes: document.querySelectorAll('iframe').length,
    adElements: document.querySelectorAll('[class*=""ad""], [id*=""ad""]').length,
    scripts: document.querySelectorAll('script[src*=""ad""], script[src*=""doubleclick""]').length
  };
  return JSON.stringify(report);
})();
";
            var result = await SiteWebView.EvaluateJavaScriptAsync(inspectJs);
            System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] After pass 1: {result}");

            // Second pass — wait for lazy-loaded / script-injected ads (500ms)
            await Task.Delay(500);
            await SiteWebView.EvaluateJavaScriptAsync(js);
            System.Diagnostics.Debug.WriteLine("[WebBrowsePage] ✓ Ad blocker pass 2 complete");

            // Third pass — catch delayed ads (1.5s)
            await Task.Delay(1000);
            await SiteWebView.EvaluateJavaScriptAsync(js);
            result = await SiteWebView.EvaluateJavaScriptAsync(inspectJs);
            System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] After pass 3: {result}");

            // Fourth pass — some sites inject ads even later (3s)
            await Task.Delay(1500);
            await SiteWebView.EvaluateJavaScriptAsync(js);
            System.Diagnostics.Debug.WriteLine("[WebBrowsePage] ✓ Ad blocker pass 4 complete");

            // Fifth pass — final cleanup (5s)
            await Task.Delay(2000);
            await SiteWebView.EvaluateJavaScriptAsync(js);
            result = await SiteWebView.EvaluateJavaScriptAsync(inspectJs);
            System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] After pass 5: {result}");

            System.Diagnostics.Debug.WriteLine("[WebBrowsePage] ===== AD BLOCKER INJECTION COMPLETE =====");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] ❌ AdBlocker inject error: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] Stack trace: {ex.StackTrace}");
        }
    }

    /// <summary>
    /// Updates the back/forward button states based on WebView navigation history.
    /// </summary>
    private void UpdateNavigationButtons()
    {
        try
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                // Back button is always enabled (either goes back in WebView or pops the page)
                BackButton.Opacity = 1.0;

                // Forward button is only enabled if WebView can go forward
                ForwardButton.Opacity = SiteWebView.CanGoForward ? 1.0 : 0.4;
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] UpdateNavigationButtons error: {ex.Message}");
        }
    }

    /// <summary>
    /// Batched, throttled update for the entire navigation bar.
    /// Combines URL bar, navigation buttons, FAB visibility, and tab count into a single UI update.
    /// Uses throttling to prevent rapid-fire updates during navigation.
    /// </summary>
    private void UpdateNavigationBar(string url)
    {
        lock (_navBarUpdateLock)
        {
            _pendingNavBarUpdateUrl = url;
            
            // Check if we should throttle
            var timeSinceLastUpdate = DateTime.Now - _lastNavBarUpdate;
            if (timeSinceLastUpdate.TotalMilliseconds < NavBarUpdateThrottleMs && !_isNavBarUpdateScheduled)
            {
                // Schedule an update after the throttle period
                _isNavBarUpdateScheduled = true;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(NavBarUpdateThrottleMs - (int)timeSinceLastUpdate.TotalMilliseconds);
                    
                    lock (_navBarUpdateLock)
                    {
                        _isNavBarUpdateScheduled = false;
                        if (_pendingNavBarUpdateUrl != null)
                        {
                            var pendingUrl = _pendingNavBarUpdateUrl;
                            _pendingNavBarUpdateUrl = null;
                            ExecuteNavigationBarUpdate(pendingUrl);
                        }
                    }
                });
                return;
            }
            
            // Execute immediately if not throttling
            if (!_isNavBarUpdateScheduled)
            {
                _pendingNavBarUpdateUrl = null;
                ExecuteNavigationBarUpdate(url);
            }
        }
    }

    /// <summary>
    /// Executes the actual navigation bar UI update on the main thread.
    /// Performs heavy operations (regex, site detection) on background thread first.
    /// </summary>
    private void ExecuteNavigationBarUpdate(string url)
    {
        _lastNavBarUpdate = DateTime.Now;
        
        // Run heavy detection operations on background thread
        _ = Task.Run(() =>
        {
            // Use cached results if available for the same URL
            bool onKnownSite;
            bool onNovelPage;
            
            if (_cachedSiteDetectionUrl == url)
            {
                // Use cached results
                onKnownSite = _cachedSiteDetectionResult != null;
                onNovelPage = _cachedNovelPageResult ?? false;
            }
            else
            {
                // Perform detection and cache results
                var siteResult = DetectSite(url);
                onKnownSite = siteResult != null;
                onNovelPage = IsNovelPage(url);
                
                _cachedSiteDetectionUrl = url;
                _cachedSiteDetectionResult = siteResult;
                _cachedNovelPageResult = onNovelPage;
            }
            
            var isTranslateActive = IsTranslateActive;
            var canGoForward = SiteWebView.CanGoForward;
            var tabCount = Math.Max(1, _tabs.Count);
            
            // Batch all UI updates into a single main thread call
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    // Update URL bar
                    UrlBarLabel.Text = url;
                    
                    // Update navigation buttons
                    BackButton.Opacity = 1.0;
                    ForwardButton.Opacity = canGoForward ? 1.0 : 0.4;
                    
                    // Update tab count badge
                    TabCountLabel.Text = tabCount.ToString();
                    
                    // Update FAB visibility (batched)
                    bool isEnglishSource = url.Contains("noveldex.io", StringComparison.OrdinalIgnoreCase);
                    FabTranslate.IsVisible = !isEnglishSource;

                    if (isTranslateActive)
                    {
                        FabDownload.IsVisible = false;
                        FabFetch.IsVisible = false;
                        FabBookmark.IsVisible = false;
                        _ = HideBookInfoPanelAsync();
                    }
                    else
                    {
                        FabDownload.IsVisible = onNovelPage;
                        FabFetch.IsVisible = onNovelPage;
                        FabBookmark.IsVisible = onNovelPage;
                        
                        if (onNovelPage)
                        {
                            UpdateBookmarkFabAppearance();
                            // Show book info panel when landing on a novel page
                            _ = ShowBookInfoPanelAsync(url);
                        }
                        else
                        {
                            // Hide panel if user navigated away from a novel page
                            _ = HideBookInfoPanelAsync();
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] Navigation bar update error: {ex.Message}");
                }
            });
        });
    }

    private async Task AnimateLoadingBarAsync()
    {
        // Animate to 85% quickly, then stall until navigation completes
        await LoadingBar.ProgressTo(0.85, 1200, Easing.CubicOut);
        while (_isLoading)
            await Task.Delay(200);
        await LoadingBar.ProgressTo(1.0, 200, Easing.Linear);
    }

    // ── FAB logic ─────────────────────────────────────────────────────────────

    // Example novel URLs per site — shown in the invalid-URL banner
    private static readonly Dictionary<string, string> _exampleUrls = new()
    {
        ["quanben.io"] = "e.g. https://www.quanben.io/n/aoshidanshen/list.html",
        ["czbooks.net"] = "e.g. https://czbooks.net/n/cp11cgi",
        ["69shuba.com"] = "e.g. https://www.69shuba.com/book/48273.htm",
        ["dmxs.org"] = "e.g. https://www.dmxs.org/book/23204.html",
        ["52shuku.net"] = "e.g. https://www.52shuku.net/xiandaidushi/08_b/bkdKE.html",
        ["situu.cc"] = "e.g. https://www.situu.cc/5_5792/",
        ["yamibo.com"] = "e.g. https://www.yamibo.com/novel/267137",
    };

    // Static counter to ensure unique instances
    private static int _instanceCounter = 0;
    private readonly int _instanceId;

    /// <summary>
    /// Returns the site key (domain) that the current URL belongs to,
    /// or null if it doesn't match any known source.
    /// </summary>
    private static string? DetectSite(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var adapter = BookService.Adapters.FirstOrDefault(a => a.Matches(url));
        return adapter?.SiteName;
    }

    /// <summary>
    /// Show the Download and Fetch FABs on any page belonging to a known source.
    /// Hidden when in translated mode to avoid URL extraction issues.
    /// Show Bookmark FAB on novel pages only.
    /// </summary>
    private void UpdateDownloadFab(string url)
    {
        // Hide Fetch/Download when in translated mode
        // User must turn off translate mode first to use these features
        if (IsTranslateActive)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                FabDownload.IsVisible = false;
                FabFetch.IsVisible = false;
                FabBookmark.IsVisible = false;
            });
            return;
        }

        // Check if we're on a known source site
        bool onKnownSite = DetectSite(url) != null;
        bool onNovelPage = IsNovelPage(url);
        bool isEnglishSource = url.Contains("noveldex.io", StringComparison.OrdinalIgnoreCase);

        MainThread.BeginInvokeOnMainThread(() =>
        {
            FabDownload.IsVisible = onNovelPage;
            FabFetch.IsVisible = onNovelPage;
            FabBookmark.IsVisible = onNovelPage; // Only show on actual novel pages
            FabTranslate.IsVisible = !isEnglishSource;

            // Update bookmark icon state
            if (onNovelPage)
            {
                UpdateBookmarkFabAppearance();
            }
        });
    }

    private async void OnFetchFabTapped(object sender, TappedEventArgs e)
    {
        await FabFetch.ScaleToAsync(0.92, 70, Easing.CubicOut);
        await FabFetch.ScaleToAsync(1.0, 70, Easing.SpringOut);

        // Collapse menu after action
        if (_fabMenuExpanded)
        {
            _fabMenuExpanded = false;
            _ = Task.WhenAll(
                FabToggleIcon.RotateToAsync(0, 250, Easing.CubicOut),
                FabMenuItems.FadeToAsync(0, 200, Easing.CubicIn)
            );
            FabMenuItems.IsVisible = false;
        }

        string url = _currentUrl;
        string site = DetectSite(url) ?? "";

        // Validate: must be a supported site and novel index page
        if (string.IsNullOrEmpty(site))
        {
            await ShowInvalidUrlBannerAsync("This site is not a supported novel source.");
            return;
        }

        if (!IsNovelPage(url))
        {
            string hint = _exampleUrls.TryGetValue(site, out var ex)
                ? $"Navigate to a novel's index page.\n{ex}"
                : "Navigate to a specific novel's index page first.";
            await ShowInvalidUrlBannerAsync(hint);
            return;
        }

        // Fire the callback so the caller (MainPage) can pre-fill its URL entry
        OnUrlFetched?.Invoke(url);

        // Pop back to the Download tab
        await Shell.Current.Navigation.PopAsync();
    }

    private async void OnDownloadFabTapped(object sender, TappedEventArgs e)
    {
        await FabDownload.ScaleToAsync(0.92, 70, Easing.CubicOut);
        await FabDownload.ScaleToAsync(1.0, 70, Easing.SpringOut);

        // Collapse menu after action
        if (_fabMenuExpanded)
        {
            _fabMenuExpanded = false;
            _ = Task.WhenAll(
                FabToggleIcon.RotateToAsync(0, 250, Easing.CubicOut),
                FabMenuItems.FadeToAsync(0, 200, Easing.CubicIn)
            );
            FabMenuItems.IsVisible = false;
        }

        string url = _currentUrl;
        string site = DetectSite(url) ?? "";

        // Validate: must be a supported site and novel index page, not just the site homepage/listing
        if (string.IsNullOrEmpty(site))
        {
            await ShowInvalidUrlBannerAsync("This site is not a supported novel source.");
            return;
        }

        if (!IsNovelPage(url))
        {
            string hint = _exampleUrls.TryGetValue(site, out var ex)
                ? $"Navigate to a novel's index page.\n{ex}"
                : "Navigate to a specific novel's index page first.";
            await ShowInvalidUrlBannerAsync(hint);
            return;
        }

        // Check for duplicate
        var existing = DownloadManager.Instance.FindExisting(url);
        if (existing != null)
        {
            string title = string.IsNullOrWhiteSpace(existing.Title) || existing.Title == "Loading..."
                ? "this novel" : $"\"{existing.Title}\"";

            bool alreadyActive = existing.Status is DownloadStatus.Downloading or DownloadStatus.Pending or DownloadStatus.Resuming or DownloadStatus.Paused;
            string message = alreadyActive
                ? $"Already downloading {title}."
                : $"{title} was already downloaded.";

            string? choice = await DisplayActionSheetAsync(message, "Stay here", null,
                "Download again", "Go to Downloads");

            if (choice == "Go to Downloads")
            {
                await Shell.Current.GoToAsync("//DownloadsPage");
                return;
            }
            if (choice != "Download again") return;

            if (existing.IsFinished)
                DownloadManager.Instance.Dismiss(existing);
        }

        DownloadManager.Instance.Enqueue(url, 0, null);
        await ShowQueuedToastAsync();
    }

    private async Task ShowInvalidUrlBannerAsync(string hint)
    {
        InvalidUrlHintLabel.Text = hint;
        InvalidUrlBanner.Opacity = 0;
        InvalidUrlBanner.TranslationY = 30;
        InvalidUrlBanner.IsVisible = true;

        await Task.WhenAll(
            InvalidUrlBanner.FadeToAsync(1.0, 250, Easing.CubicOut),
            InvalidUrlBanner.TranslateToAsync(0, 0, 250, Easing.CubicOut));

        await Task.Delay(4000);

        await Task.WhenAll(
            InvalidUrlBanner.FadeToAsync(0, 250, Easing.CubicIn),
            InvalidUrlBanner.TranslateToAsync(0, 30, 250, Easing.CubicIn));

        InvalidUrlBanner.IsVisible = false;
    }

    private async Task ShowQueuedToastAsync(string message = "Queued for download!")
    {
        QueuedToastLabel.Text = message;
        QueuedToast.Opacity = 0;
        QueuedToast.TranslationY = 20;
        QueuedToast.IsVisible = true;

        await Task.WhenAll(
            QueuedToast.FadeToAsync(1.0, 250, Easing.CubicOut),
            QueuedToast.TranslateToAsync(0, 0, 250, Easing.CubicOut));

        await Task.Delay(2500);

        await Task.WhenAll(
            QueuedToast.FadeToAsync(0, 250, Easing.CubicIn),
            QueuedToast.TranslateToAsync(0, 20, 250, Easing.CubicIn));

        QueuedToast.IsVisible = false;
    }

    // ── FAB menu toggle ───────────────────────────────────────────────────────

    private async void OnFabToggleTapped(object sender, TappedEventArgs e)
    {
        await FabToggle.ScaleToAsync(0.92, 70, Easing.CubicOut);
        await FabToggle.ScaleToAsync(1.0, 70, Easing.SpringOut);

        _fabMenuExpanded = !_fabMenuExpanded;

        if (_fabMenuExpanded)
        {
            // Expand menu
            FabMenuItems.IsVisible = true;

            // Animate icon rotation (arrow pointing down)
            await Task.WhenAll(
                FabToggleIcon.RotateToAsync(180, 250, Easing.CubicOut),
                FabMenuItems.FadeToAsync(1.0, 200, Easing.CubicOut),
                FabMenuItems.TranslateToAsync(0, 0, 200, Easing.CubicOut)
            );
        }
        else
        {
            // Collapse menu
            await Task.WhenAll(
                FabToggleIcon.RotateToAsync(0, 250, Easing.CubicOut),
                FabMenuItems.FadeToAsync(0, 200, Easing.CubicIn),
                FabMenuItems.TranslateToAsync(0, 20, 200, Easing.CubicIn)
            );

            FabMenuItems.IsVisible = false;
        }
    }

    // ── Bookmark logic ────────────────────────────────────────────────────────

    private async void OnBookmarkTapped(object sender, TappedEventArgs e)
    {
        try
        {
            await FabBookmark.ScaleToAsync(0.92, 70, Easing.CubicOut);
            await FabBookmark.ScaleToAsync(1.0, 70, Easing.SpringOut);

            string url = _currentUrl;

            // Validate: must be a novel page
            if (!IsNovelPage(url))
            {
                await DisplayAlertAsync("Cannot Bookmark",
                    "Navigate to a specific novel's index page first.", "OK");
                return;
            }

            string? site = DetectSite(url);
            if (site == null)
            {
                await DisplayAlertAsync("Cannot Bookmark",
                    "This site is not supported for bookmarks.", "OK");
                return;
            }

            // Check if already bookmarked
            bool isBookmarked = BookmarkService.Instance.IsBookmarked(url);

            if (isBookmarked)
            {
                // Remove bookmark
                BookmarkService.Instance.RemoveBookmark(url);
                UpdateBookmarkFabAppearance();
                await ShowQueuedToastAsync("Bookmark removed!");
            }
            else
            {
                // Add bookmark - need to fetch title and author
                await AddBookmarkAsync(url, site);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] Bookmark error: {ex.Message}");
            await DisplayAlertAsync("Bookmark Error",
                $"An error occurred:\n{ex.Message}", "OK");
        }
    }

    // ── Book Info Panel ───────────────────────────────────────────────────────

    /// <summary>
    /// Slides up the book info panel and fetches/translates title, author and cover
    /// for <paramref name="url"/>. Re-shows automatically on every navigation to a
    /// novel page — even if the user closed it before — unless they are still on the
    /// exact same URL they dismissed it from. Uses a per-URL cache so returning to a
    /// previously visited novel skips the network round-trip.
    /// </summary>
    private async Task ShowBookInfoPanelAsync(string url)
    {
        // Already open and showing this exact URL — nothing to do
        if (_isBookInfoPanelOpen && _bookInfoPanelUrl == url)
            return;

        // Cancel any previous in-flight fetch
        _bookInfoCts?.Cancel();
        _bookInfoCts?.Dispose();
        _bookInfoCts = new CancellationTokenSource();
        var ct = _bookInfoCts.Token;

        _bookInfoPanelUrl = url;

        // ── Populate from cache or show loading ──────────────────────────────
        if (_bookInfoCache.TryGetValue(url, out var cached))
        {
            BookInfoLoading.IsVisible = false;
            BookInfoOriginalTitle.Text = cached.Title;
            BookInfoTranslatedTitle.Text = cached.TranslatedTitle;
            BookInfoTranslatedTitle.IsVisible = !string.IsNullOrWhiteSpace(cached.TranslatedTitle);
            BookInfoAuthor.Text = cached.Author;
            BookInfoAuthorTranslated.Text = string.IsNullOrWhiteSpace(cached.TranslatedAuthor)
                ? "" : $"({cached.TranslatedAuthor})";
            BookInfoAuthorTranslated.IsVisible = !string.IsNullOrWhiteSpace(cached.TranslatedAuthor);
        }
        else
        {
            BookInfoOriginalTitle.Text = "";
            BookInfoTranslatedTitle.Text = "";
            BookInfoTranslatedTitle.IsVisible = false;
            BookInfoAuthor.Text = "";
            BookInfoAuthorTranslated.Text = "";
            BookInfoAuthorTranslated.IsVisible = false;
            BookInfoLoading.IsVisible = true;
        }

        // ── Animate panel in (always — even after user closed it) ────────────
        _isBookInfoPanelOpen = true;
        BookInfoPanel.IsVisible = true;
        BookInfoPanel.TranslationY = 120;
        BookInfoPanel.Opacity = 0;
        await Task.WhenAll(
            BookInfoPanel.TranslateToAsync(0, 0, 220, Easing.CubicOut),
            BookInfoPanel.FadeToAsync(1, 200, Easing.CubicOut));

        // ── Fetch + translate if not cached ──────────────────────────────────
        if (_bookInfoCache.ContainsKey(url))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                if (ct.IsCancellationRequested) return;

                var bookService = new BookService(new WebViewCloudflareBypass());
                var bookInfo = await bookService.GatherBookInfo(url, 0, null, ct: ct);

                if (ct.IsCancellationRequested || bookInfo == null) return;

                string origTitle  = bookInfo.Title  ?? "";
                string origAuthor = bookInfo.Author ?? "";

                string translatedTitle  = "";
                string translatedAuthor = "";
                try
                {
                    using var http = new System.Net.Http.HttpClient();
                    http.Timeout = TimeSpan.FromSeconds(15);
                    http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                        "Mozilla/5.0 (Linux; Android 10; Mobile) AppleWebKit/537.36 Chrome/124.0 Mobile Safari/537.36");
                    var translator = new Shuka.Core.Translator(http);

                    if (!string.IsNullOrWhiteSpace(origTitle))
                        translatedTitle = await translator.Translate(origTitle, null, ct);
                    if (!string.IsNullOrWhiteSpace(origAuthor))
                        translatedAuthor = await translator.Translate(origAuthor, null, ct);

                    if (string.Equals(translatedTitle,  origTitle,  StringComparison.OrdinalIgnoreCase))
                        translatedTitle  = "";
                    if (string.Equals(translatedAuthor, origAuthor, StringComparison.OrdinalIgnoreCase))
                        translatedAuthor = "";
                }
                catch { /* translation optional */ }

                if (ct.IsCancellationRequested) return;

                _bookInfoCache[url] = new BookInfoCache(
                    origTitle, translatedTitle, origAuthor, translatedAuthor);

                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    BookInfoLoading.IsVisible = false;
                    BookInfoOriginalTitle.Text = origTitle;
                    BookInfoTranslatedTitle.Text = translatedTitle;
                    BookInfoTranslatedTitle.IsVisible = !string.IsNullOrWhiteSpace(translatedTitle);
                    BookInfoAuthor.Text = origAuthor;
                    BookInfoAuthorTranslated.Text = string.IsNullOrWhiteSpace(translatedAuthor)
                        ? "" : $"({translatedAuthor})";
                    BookInfoAuthorTranslated.IsVisible = !string.IsNullOrWhiteSpace(translatedAuthor);
                });
            }
            catch (OperationCanceledException) { /* navigated away */ }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] BookInfoPanel fetch: {ex.Message}");
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    if (!ct.IsCancellationRequested)
                        BookInfoLoading.IsVisible = false;
                });
            }
        }, ct);
    }

    private async Task HideBookInfoPanelAsync()
    {
        if (!_isBookInfoPanelOpen) return;
        _isBookInfoPanelOpen = false;
        // Don't cancel _bookInfoCts here — ShowBookInfoPanelAsync manages its own lifecycle.
        // Cancelling here would kill a fetch that ShowBookInfoPanelAsync just kicked off
        // if Hide and Show are called in quick succession (e.g. navigating away then back).

        await Task.WhenAll(
            BookInfoPanel.TranslateToAsync(0, 120, 180, Easing.CubicIn),
            BookInfoPanel.FadeToAsync(0, 160, Easing.CubicIn));
        BookInfoPanel.IsVisible = false;
    }

    private async void OnBookInfoPanelCloseTapped(object sender, TappedEventArgs e)
    {
        await HideBookInfoPanelAsync();
    }

    /// <summary>
    /// Fetches the novel's title and author, then adds it to bookmarks.
    /// </summary>
    private async Task AddBookmarkAsync(string url, string siteName)
    {
        try
        {
            // Show loading state
            FabBookmarkLabel.Text = "LOADING...";
            FabBookmark.IsEnabled = false;

            // Fetch book info (title and author in Chinese) using BookService with Cloudflare bypass
            var bookService = new BookService(new WebViewCloudflareBypass());
            var bookInfo = await bookService.GatherBookInfo(url, 0, null);

            if (bookInfo == null || string.IsNullOrWhiteSpace(bookInfo.Title))
            {
                await DisplayAlertAsync("Error",
                    "Could not fetch novel information. Please try again.", "OK");
                return;
            }

            // Add to bookmarks with Chinese title and author
            BookmarkService.Instance.AddBookmark(
                url,
                bookInfo.Title,
                bookInfo.Author ?? "Unknown",
                siteName,
                bookInfo.Total,
                bookInfo.CoverUrl);

            UpdateBookmarkFabAppearance();
            await ShowQueuedToastAsync($"Bookmarked: {bookInfo.Title}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebBrowsePage] AddBookmark error: {ex.Message}");
            await DisplayAlertAsync("Error",
                $"Could not add bookmark:\n{ex.Message}", "OK");
        }
        finally
        {
            // Restore button state
            FabBookmark.IsEnabled = true;
            UpdateBookmarkFabAppearance();
        }
    }

    /// <summary>
    /// Updates the Bookmark FAB to show bookmarked/not-bookmarked state.
    /// </summary>
    private void UpdateBookmarkFabAppearance()
    {
        bool isBookmarked = BookmarkService.Instance.IsBookmarked(_currentUrl);

        if (isBookmarked)
        {
            // Bookmarked state: filled icon, accent color
            FabBookmark.SetDynamicResource(Border.BackgroundColorProperty, "AccentContainer");
            FabBookmark.SetDynamicResource(Border.StrokeProperty, "AccentLight");
            FabBookmarkIcon.Text = "\uE866"; // bookmark filled
            FabBookmarkIcon.SetDynamicResource(Label.TextColorProperty, "AccentLight");
            FabBookmarkLabel.SetDynamicResource(Label.TextColorProperty, "AccentLight");
            FabBookmarkLabel.Text = "BOOKMARKED";
        }
        else
        {
            // Not bookmarked: outlined icon, muted color
            FabBookmark.SetDynamicResource(Border.BackgroundColorProperty, "BgCard");
            FabBookmark.SetDynamicResource(Border.StrokeProperty, "Stroke");
            FabBookmarkIcon.Text = "\uE867"; // bookmark outlined
            FabBookmarkIcon.SetDynamicResource(Label.TextColorProperty, "TextSecondary");
            FabBookmarkLabel.SetDynamicResource(Label.TextColorProperty, "TextSecondary");
            FabBookmarkLabel.Text = "BOOKMARK";
        }
    }

    private void AddTab(string initialUrl, bool switchToTab)
    {
        var tab = new BrowserTab
        {
            Url = initialUrl,
            Title = GetReadableTabTitle(initialUrl),
            LastTouchedAt = DateTime.Now
        };
        _tabs.Add(tab);
        if (switchToTab)
            _activeTabId = tab.Id;
        UpdateTabCountBadge();
    }

    private void SwitchToTab(Guid tabId)
    {
        var tab = _tabs.FirstOrDefault(t => t.Id == tabId);
        if (tab == null)
            return;

        _activeTabId = tabId;
        tab.LastTouchedAt = DateTime.Now;
        Navigate(tab.Url);
        UpdateTabCountBadge();
    }

    private async Task CloseActiveTabAsync()
    {
        if (_tabs.Count <= 1)
        {
            await ShowQueuedToastAsync("At least one tab must stay open.");
            return;
        }

        var current = _tabs.FirstOrDefault(t => t.Id == _activeTabId);
        if (current != null)
            _tabs.Remove(current);

        var next = _tabs.OrderByDescending(t => t.LastTouchedAt).First();
        _activeTabId = next.Id;
        Navigate(next.Url);
        UpdateTabCountBadge();
    }

    private void CloseOtherTabs()
    {
        var current = _tabs.FirstOrDefault(t => t.Id == _activeTabId);
        if (current == null)
            return;

        _tabs.Clear();
        _tabs.Add(current);
        current.LastTouchedAt = DateTime.Now;
        UpdateTabCountBadge();
    }

    private void UpdateActiveTab(string? url, string? title)
    {
        var active = _tabs.FirstOrDefault(t => t.Id == _activeTabId);
        if (active == null)
            return;

        if (!string.IsNullOrWhiteSpace(url))
            active.Url = url!;
        if (!string.IsNullOrWhiteSpace(title))
            active.Title = title!;
        else if (string.IsNullOrWhiteSpace(active.Title))
            active.Title = GetReadableTabTitle(active.Url);
        active.LastTouchedAt = DateTime.Now;
    }

    private void UpdateTabCountBadge()
    {
        TabCountLabel.Text = Math.Max(1, _tabs.Count).ToString();
        if (_isTabOverviewOpen)
            RebuildTabOverviewList();
    }

    private static string BuildTabLabel(BrowserTab tab)
    {
        var title = string.IsNullOrWhiteSpace(tab.Title)
            ? GetReadableTabTitle(tab.Url)
            : tab.Title.Trim();
        return title.Length > 32 ? title[..32] + "..." : title;
    }

    private static string GetReadableTabTitle(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "New Tab";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;
        return string.IsNullOrWhiteSpace(uri.Host) ? url : uri.Host;
    }

    private void AddRecentVisit(string? url, string? title)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return;

        string source = DetectSite(url) ?? uri.Host;
        var latest = _recentVisits.FirstOrDefault();
        if (latest != null && latest.Url.Equals(url, StringComparison.OrdinalIgnoreCase))
            return;

        _recentVisits.Insert(0, new WebVisitEntry
        {
            Url = url,
            Source = source,
            Title = string.IsNullOrWhiteSpace(title) ? GetReadableTabTitle(url) : title.Trim(),
            VisitedAt = DateTime.Now
        });

        if (_recentVisits.Count > 120)
            _recentVisits = _recentVisits.Take(120).ToList();
        _ = SaveRecentVisitsAsync();
    }

    private async Task ShowRecentHistoryAsync()
    {
        await ShowRecentLinksSheetAsync();
    }

    private static List<WebVisitEntry> LoadRecentVisits()
    {
        try
        {
            if (!File.Exists(WebHistoryFile))
                return new List<WebVisitEntry>();

            var json = File.ReadAllText(WebHistoryFile);
            var list = JsonSerializer.Deserialize<List<WebVisitEntry>>(json);
            return list?.OrderByDescending(v => v.VisitedAt).Take(120).ToList()
                ?? new List<WebVisitEntry>();
        }
        catch
        {
            return new List<WebVisitEntry>();
        }
    }

    private async Task SaveRecentVisitsAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(_recentVisits.Take(120).ToList());
            await File.WriteAllTextAsync(WebHistoryFile, json);
        }
        catch
        {
            // ignore write failures for non-critical history persistence
        }
    }

    private async Task ShowTabOverviewAsync()
    {
        _isTabOverviewOpen = true;
        RebuildTabOverviewList();
        TabOverviewOverlay.Opacity = 0;
        TabOverviewOverlay.IsVisible = true;
        await TabOverviewOverlay.FadeToAsync(1, 180, Easing.CubicOut);
    }

    private async Task HideTabOverviewAsync()
    {
        if (!_isTabOverviewOpen)
            return;
        _isTabOverviewOpen = false;
        await TabOverviewOverlay.FadeToAsync(0, 150, Easing.CubicIn);
        TabOverviewOverlay.IsVisible = false;
    }

    private void RebuildTabOverviewList()
    {
        if (TabOverviewList == null)
            return;

        var orderedTabs = _tabs
            .OrderByDescending(t => t.Id == _activeTabId)
            .ThenByDescending(t => t.LastTouchedAt)
            .ToList();

        TabOverviewList.Children.Clear();
        TabOverviewCountLabel.Text = _tabs.Count == 1 ? "1 tab" : $"{_tabs.Count} tabs";

        foreach (var tab in orderedTabs)
        {
            bool isActive = tab.Id == _activeTabId;
            var row = new Border
            {
                StrokeThickness = 1,
                Padding = new Thickness(12, 10),
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 }
            };
            row.SetDynamicResource(Border.BackgroundColorProperty, isActive ? "AccentContainer" : "BgInput");
            row.SetDynamicResource(Border.StrokeProperty, isActive ? "AccentLight" : "Stroke");
            row.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(async () =>
                {
                    SwitchToTab(tab.Id);
                    await HideTabOverviewAsync();
                })
            });

            var layout = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto)
                },
                ColumnSpacing = 8
            };

            var textWrap = new VerticalStackLayout { Spacing = 2 };
            var titleLabel = new Label
            {
                Text = BuildTabLabel(tab),
                FontSize = 13,
                FontAttributes = FontAttributes.Bold
            };
            titleLabel.SetDynamicResource(Label.TextColorProperty, isActive ? "AccentLight" : "TextPrimary");

            var urlLabel = new Label
            {
                Text = tab.Url,
                FontSize = 10,
                MaxLines = 1,
                LineBreakMode = LineBreakMode.TailTruncation
            };
            urlLabel.SetDynamicResource(Label.TextColorProperty, isActive ? "AccentLight" : "TextMuted");

            textWrap.Children.Add(titleLabel);
            textWrap.Children.Add(urlLabel);
            layout.Add(textWrap, 0, 0);

            var openButton = new Button
            {
                Text = "OPEN",
                FontSize = 10,
                Padding = new Thickness(10, 6),
                CornerRadius = 12,
                ClassId = $"open:{tab.Id:D}",
                BackgroundColor = Colors.Transparent
            };
            openButton.SetDynamicResource(Button.TextColorProperty, isActive ? "AccentLight" : "TextSecondary");
            openButton.Clicked += OnTabCardActionClicked;
            layout.Add(openButton, 1, 0);

            var closeButton = new Button
            {
                Text = "\u2715",
                FontSize = 10,
                Padding = new Thickness(10, 6),
                CornerRadius = 12,
                ClassId = $"close:{tab.Id:D}",
                BackgroundColor = Colors.Transparent,
                IsVisible = _tabs.Count > 1
            };
            closeButton.SetDynamicResource(Button.TextColorProperty, isActive ? "AccentLight" : "TextMuted");
            closeButton.Clicked += OnTabCardActionClicked;
            layout.Add(closeButton, 2, 0);

            row.Content = layout;
            TabOverviewList.Children.Add(row);
        }
    }

    private async void OnTabCardActionClicked(object? sender, EventArgs e)
    {
        if (sender is not Button btn || string.IsNullOrWhiteSpace(btn.ClassId))
            return;

        var parts = btn.ClassId.Split(':', 2);
        if (parts.Length != 2 || !Guid.TryParse(parts[1], out var tabId))
            return;

        if (parts[0] == "open")
        {
            SwitchToTab(tabId);
            await HideTabOverviewAsync();
            return;
        }

        if (parts[0] == "close")
        {
            if (_tabs.Count <= 1)
                return;

            var target = _tabs.FirstOrDefault(t => t.Id == tabId);
            if (target == null)
                return;

            bool wasActive = target.Id == _activeTabId;
            _tabs.Remove(target);

            if (wasActive)
            {
                var fallback = _tabs.OrderByDescending(t => t.LastTouchedAt).First();
                _activeTabId = fallback.Id;
                Navigate(fallback.Url);
            }

            UpdateTabCountBadge();
        }
    }

    private async void OnTabOverviewNewTabTapped(object sender, TappedEventArgs e)
    {
        AddTab(_homeUrl, switchToTab: true);
        Navigate(_homeUrl);
        await HideTabOverviewAsync();
    }

    private void OnTabOverviewCloseOthersTapped(object sender, TappedEventArgs e)
    {
        CloseOtherTabs();
        RebuildTabOverviewList();
    }

    private async void OnTabOverviewRecentTapped(object sender, TappedEventArgs e)
    {
        await ShowRecentHistoryAsync();
        RebuildTabOverviewList();
    }

    private async void OnTabOverviewDoneTapped(object sender, TappedEventArgs e)
    {
        await HideTabOverviewAsync();
    }

    private void RefreshBrowserMenuState()
    {
        bool enabled = AdBlockerService.Instance.IsEnabled;
        BrowserMenuAdBlockSubtitle.Text = enabled ? "Currently filtering ads on pages." : "Disabled for all pages.";
        BrowserMenuAdBlockState.Text = enabled ? "ON" : "OFF";
        BrowserMenuAdBlockRow.SetDynamicResource(
            Border.BackgroundColorProperty, enabled ? "AccentContainer" : "BgInput");
        BrowserMenuAdBlockRow.SetDynamicResource(
            Border.StrokeProperty, enabled ? "AccentLight" : "Stroke");
        BrowserMenuAdBlockIcon.SetDynamicResource(
            Label.TextColorProperty, enabled ? "AccentLight" : "TextMuted");
        BrowserMenuAdBlockState.SetDynamicResource(
            Label.TextColorProperty, enabled ? "AccentLight" : "TextMuted");
    }

    private async Task ShowBrowserMenuAsync()
    {
        if (_isBrowserMenuOpen)
            return;

        _isBrowserMenuOpen = true;
        RefreshBrowserMenuState();
        BrowserMenuOverlay.IsVisible = true;
        BrowserMenuOverlay.Opacity = 0;
        BrowserMenuSheet.Opacity = 0;
        BrowserMenuSheet.TranslationY = 30;

        await Task.WhenAll(
            BrowserMenuOverlay.FadeToAsync(1, 160, Easing.CubicOut),
            BrowserMenuSheet.FadeToAsync(1, 180, Easing.CubicOut),
            BrowserMenuSheet.TranslateToAsync(0, 0, 180, Easing.CubicOut));
    }

    private async Task HideBrowserMenuAsync()
    {
        if (!_isBrowserMenuOpen)
            return;

        _isBrowserMenuOpen = false;
        await Task.WhenAll(
            BrowserMenuSheet.FadeToAsync(0, 140, Easing.CubicIn),
            BrowserMenuSheet.TranslateToAsync(0, 24, 140, Easing.CubicIn),
            BrowserMenuOverlay.FadeToAsync(0, 140, Easing.CubicIn));
        BrowserMenuOverlay.IsVisible = false;
    }

    private async void OnBrowserMenuOverlayTapped(object sender, TappedEventArgs e)
    {
        await HideBrowserMenuAsync();
    }

    private void OnBrowserMenuSheetTapped(object sender, TappedEventArgs e)
    {
        // Prevent taps inside the sheet from dismissing via overlay tap handler.
    }

    private async void OnBrowserMenuCloseTapped(object sender, TappedEventArgs e)
    {
        await HideBrowserMenuAsync();
    }

    private async void OnBrowserMenuRecentTapped(object sender, TappedEventArgs e)
    {
        await HideBrowserMenuAsync();
        await ShowRecentHistoryAsync();
    }

    private async void OnBrowserMenuAdBlockTapped(object sender, TappedEventArgs e)
    {
        OnAdBlockerToggleTapped();
        RefreshBrowserMenuState();
        await HideBrowserMenuAsync();
    }

    private async void OnBrowserMenuRefreshTapped(object sender, TappedEventArgs e)
    {
        SiteWebView.Reload();
        await HideBrowserMenuAsync();
    }

    private async void OnBrowserMenuHomeTapped(object sender, TappedEventArgs e)
    {
        if (IsTranslateActive)
        {
            _translateMode = WebTranslateMode.None;
            _originalUrl = string.Empty;
            UpdateTranslateFabAppearance();
        }
        Navigate(_homeUrl);
        await HideBrowserMenuAsync();
    }

    private async void OnBrowserMenuOpenInBrowserTapped(object sender, TappedEventArgs e)
    {
        try { await Launcher.Default.OpenAsync(new Uri(_currentUrl)); }
        catch { }
        await HideBrowserMenuAsync();
    }

    private void RebuildRecentLinksList()
    {
        RecentLinksList.Children.Clear();
        var top = _recentVisits.Take(30).ToList();
        RecentLinksCountLabel.Text = top.Count == 1 ? "1 link" : $"{top.Count} links";

        if (top.Count == 0)
        {
            var empty = new Label
            {
                Text = "No recent links yet.",
                FontSize = 12,
                HorizontalOptions = LayoutOptions.Center,
                Margin = new Thickness(0, 12)
            };
            empty.SetDynamicResource(Label.TextColorProperty, "TextMuted");
            RecentLinksList.Children.Add(empty);
            return;
        }

        foreach (var visit in top)
        {
            var row = new Border
            {
                StrokeThickness = 1,
                Padding = new Thickness(12, 10),
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 }
            };
            row.SetDynamicResource(Border.BackgroundColorProperty, "BgInput");
            row.SetDynamicResource(Border.StrokeProperty, "Stroke");
            row.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(async () =>
                {
                    AddTab(visit.Url, switchToTab: true);
                    Navigate(visit.Url);
                    await HideRecentLinksSheetAsync();
                })
            });

            var grid = new Grid
            {
                RowDefinitions = new RowDefinitionCollection
                {
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto)
                },
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto)
                }
            };

            var title = new Label
            {
                Text = string.IsNullOrWhiteSpace(visit.Title) ? GetReadableTabTitle(visit.Url) : visit.Title!,
                FontSize = 12,
                FontAttributes = FontAttributes.Bold,
                MaxLines = 1,
                LineBreakMode = LineBreakMode.TailTruncation
            };
            title.SetDynamicResource(Label.TextColorProperty, "TextPrimary");
            grid.Add(title, 0, 0);

            var source = new Label
            {
                Text = visit.Source,
                FontSize = 10
            };
            source.SetDynamicResource(Label.TextColorProperty, "AccentLight");
            grid.Add(source, 1, 0);

            var url = new Label
            {
                Text = visit.Url,
                FontSize = 10,
                MaxLines = 1,
                LineBreakMode = LineBreakMode.TailTruncation
            };
            url.SetDynamicResource(Label.TextColorProperty, "TextMuted");
            grid.Add(url, 0, 1);
            Grid.SetColumnSpan(url, 2);

            row.Content = grid;
            RecentLinksList.Children.Add(row);
        }
    }

    private async Task ShowRecentLinksSheetAsync()
    {
        if (_isRecentLinksOpen)
            return;
        _isRecentLinksOpen = true;
        RebuildRecentLinksList();
        RecentLinksOverlay.IsVisible = true;
        RecentLinksOverlay.Opacity = 0;
        RecentLinksSheet.Opacity = 0;
        RecentLinksSheet.TranslationY = 30;

        await Task.WhenAll(
            RecentLinksOverlay.FadeToAsync(1, 160, Easing.CubicOut),
            RecentLinksSheet.FadeToAsync(1, 180, Easing.CubicOut),
            RecentLinksSheet.TranslateToAsync(0, 0, 180, Easing.CubicOut));
    }

    private async Task HideRecentLinksSheetAsync()
    {
        if (!_isRecentLinksOpen)
            return;
        _isRecentLinksOpen = false;
        await Task.WhenAll(
            RecentLinksSheet.FadeToAsync(0, 140, Easing.CubicIn),
            RecentLinksSheet.TranslateToAsync(0, 24, 140, Easing.CubicIn),
            RecentLinksOverlay.FadeToAsync(0, 140, Easing.CubicIn));
        RecentLinksOverlay.IsVisible = false;
    }

    private async void OnRecentLinksOverlayTapped(object sender, TappedEventArgs e)
    {
        await HideRecentLinksSheetAsync();
    }

    private void OnRecentLinksSheetTapped(object sender, TappedEventArgs e)
    {
        // Swallow tap so overlay handler does not close it.
    }

    private async void OnRecentLinksCloseTapped(object sender, TappedEventArgs e)
    {
        await HideRecentLinksSheetAsync();
    }

    private async void OnRecentLinksClearTapped(object sender, TappedEventArgs e)
    {
        _recentVisits.Clear();
        await SaveRecentVisitsAsync();
        RebuildRecentLinksList();
        await ShowQueuedToastAsync("Recent links cleared.");
    }

    private async Task<string?> ShowCloudflareTranslateSheetAsync(string site)
    {
        if (_isCloudflareSheetOpen)
            return null;

        _isCloudflareSheetOpen = true;
        CloudflareSheetSubtitle.Text = $"{site} is Cloudflare protected. Use browser actions below.";
        _cloudflareChoiceTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        CloudflareSheetOverlay.IsVisible = true;
        CloudflareSheetOverlay.Opacity = 0;
        CloudflareSheet.Opacity = 0;
        CloudflareSheet.TranslationY = 30;

        await Task.WhenAll(
            CloudflareSheetOverlay.FadeToAsync(1, 160, Easing.CubicOut),
            CloudflareSheet.FadeToAsync(1, 180, Easing.CubicOut),
            CloudflareSheet.TranslateToAsync(0, 0, 180, Easing.CubicOut));

        return await _cloudflareChoiceTcs.Task;
    }

    private async Task HideCloudflareTranslateSheetAsync(string? result = null)
    {
        if (!_isCloudflareSheetOpen)
            return;

        _isCloudflareSheetOpen = false;
        await Task.WhenAll(
            CloudflareSheet.FadeToAsync(0, 140, Easing.CubicIn),
            CloudflareSheet.TranslateToAsync(0, 24, 140, Easing.CubicIn),
            CloudflareSheetOverlay.FadeToAsync(0, 140, Easing.CubicIn));
        CloudflareSheetOverlay.IsVisible = false;
        _cloudflareChoiceTcs?.TrySetResult(result);
        _cloudflareChoiceTcs = null;
    }

    private async void OnCloudflareSheetOverlayTapped(object sender, TappedEventArgs e)
    {
        await HideCloudflareTranslateSheetAsync();
    }

    private void OnCloudflareSheetTapped(object sender, TappedEventArgs e)
    {
        // Swallow tap so overlay handler does not close it.
    }

    private async void OnCloudflareSheetCancelTapped(object sender, TappedEventArgs e)
    {
        await HideCloudflareTranslateSheetAsync();
    }

    private async void OnCloudflareTranslateBrowserTapped(object sender, TappedEventArgs e)
    {
        await HideCloudflareTranslateSheetAsync(CloudflareChoiceTranslateBrowser);
    }

    private async void OnCloudflareCopyUrlTapped(object sender, TappedEventArgs e)
    {
        await HideCloudflareTranslateSheetAsync(CloudflareChoiceCopyUrl);
    }

    private async void OnCloudflareOpenBrowserTapped(object sender, TappedEventArgs e)
    {
        await HideCloudflareTranslateSheetAsync(CloudflareChoiceOpenBrowser);
    }

    private void UpdateBottomSheetMargins()
    {
        double bottomInset = 16;
#if ANDROID
        if (MainActivity.Instance is { } activity)
            bottomInset = Math.Max(bottomInset, activity.GetOverlayBottomInsetDip(14));
#endif

        BrowserMenuSheet.Margin = new Thickness(12, 0, 12, bottomInset);
        RecentLinksSheet.Margin = new Thickness(12, 0, 12, bottomInset);
        CloudflareSheet.Margin = new Thickness(12, 0, 12, bottomInset);
    }
}
