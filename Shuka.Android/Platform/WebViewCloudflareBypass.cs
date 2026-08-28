using System.Text.RegularExpressions;
using Shuka.Core;

namespace Shuka.Android.Platform;

/// <summary>
/// Android implementation of ICloudflareBypass.
/// Uses a hidden MAUI WebView to load pages and extract rendered HTML
/// after Cloudflare's JS challenge completes or Next.js SPA hydrates.
/// Handles Shell navigation, tab pages, and direct ContentPages.
/// </summary>
public class WebViewCloudflareBypass : ICloudflareBypass
{
    // Max time to wait for CF challenge + real page to load
    private const int MaxWaitMs   = 35000;
    private const int PollMs      = 1000;

    // Serialize all WebView fetches — CF cookie must be established before
    // the next fetch starts. Multiple concurrent WebViews cause CF to re-challenge.
    private static readonly SemaphoreSlim _sem = new(1, 1);

    // Persistent WebView reused for Noveldex chapter fetches.
    // Navigating one long-lived WebView avoids WebKit DOM accumulation that
    // causes Android to freeze after 15+ separate WebView instances.
    private static WebView?   _persistentWebView;
    private static Grid?      _persistentOverlay;
    private static Layout?    _persistentHost;

    /// <summary>
    /// Returns cookies stored by Android's WebKit CookieManager for a given host.
    /// After a successful WebView bypass for noveldex.io, the CF clearance cookie
    /// is stored here and reused by HttpFetcher for all subsequent direct requests,
    /// eliminating the need to spin up a WebView for every chapter fetch.
    /// </summary>
    public string? GetCookies(string host)
    {
#if ANDROID
        try
        {
            var cm = global::Android.Webkit.CookieManager.Instance;
            if (cm == null) return null;

            // CookieManager.GetCookie takes a URL string, not just a hostname
            string? cookies = cm.GetCookie($"https://{host}");
            if (string.IsNullOrWhiteSpace(cookies)) return null;
            return cookies;
        }
        catch { return null; }
#else
        return null;
#endif
    }

    public async Task<string> FetchAsync(string url, CancellationToken ct = default)
    {
        await _sem.WaitAsync(ct);
        try
        {
            // Noveldex chapter pages: reuse a persistent WebView to avoid memory accumulation
            bool isNoveldexChapter = Regex.IsMatch(url,
                @"noveldex\.io/series/.+/chapter/\d+", RegexOptions.IgnoreCase);

            if (isNoveldexChapter)
                return await FetchWithPersistentWebViewAsync(url, ct);

            return await FetchInternalAsync(url, ct);
        }
        finally
        {
            _sem.Release();
        }
    }

    /// <summary>
    /// Navigates the persistent singleton WebView to a new URL and waits for content.
    /// The same WebView instance is reused across all Noveldex chapter fetches, so
    /// WebKit never accumulates stale DOM trees from hundreds of separate instances.
    /// </summary>
    private Task<string> FetchWithPersistentWebViewAsync(string url, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                // Create and attach the persistent WebView once
                if (_persistentWebView == null)
                {
                    _persistentWebView = new WebView
                    {
                        IsVisible        = true,
                        Opacity          = 0.01,
                        InputTransparent = true,
                        WidthRequest     = 1,
                        HeightRequest    = 1,
                    };

#if ANDROID
                    _persistentWebView.HandlerChanged += (s, e) =>
                    {
                        if (_persistentWebView?.Handler?.PlatformView is global::Android.Webkit.WebView awv)
                        {
                            try
                            {
                                awv.Settings.JavaScriptEnabled  = true;
                                awv.Settings.DomStorageEnabled  = true;
                                awv.Settings.DatabaseEnabled    = true;
                                awv.Settings.SetSupportMultipleWindows(false);
                                awv.Settings.JavaScriptCanOpenWindowsAutomatically = false;
                                awv.Settings.UserAgentString    = "Mozilla/5.0 (Linux; Android 10; SM-G973F) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Mobile Safari/537.36";
                                awv.OnResume();
                                awv.ResumeTimers();
                            }
                            catch { }
                        }
                    };
#endif

                    (_persistentHost, _persistentOverlay) = AttachWebView(_persistentWebView);
                }

                // Navigate to the new chapter URL
                _persistentWebView.Source = new UrlWebViewSource { Url = url };

                using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

                // Brief settle delay then poll
                await Task.Delay(1200, ct);

                string? html = null;
                int waited = 0;

                while (waited < MaxWaitMs)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(PollMs, ct);
                    waited += PollMs;

#if ANDROID
                    if (_persistentWebView.Handler?.PlatformView is global::Android.Webkit.WebView awv2)
                    {
                        try { awv2.OnResume(); awv2.ResumeTimers(); } catch { }
                    }
#endif

                    html = await GetPageHtmlAsync(_persistentWebView);
                    ct.ThrowIfCancellationRequested();
                    if (html == null || html.Length < 300) continue;

                    bool isChallenge =
                        html.Contains("cf-chl-opt") ||
                        html.Contains("cf-browser-verification") ||
                        html.Contains("jschl-answer") ||
                        html.Contains("challenge-form") ||
                        (html.Contains("cloudflare") && html.Contains("checking"));

                    if (!isChallenge) break;
                }

                // Wait for Next.js chapter content to hydrate
                await WaitForNoveldexChapterAsync(_persistentWebView, ct);
                ct.ThrowIfCancellationRequested();
                html = await GetPageHtmlAsync(_persistentWebView);

                tcs.TrySetResult(html ?? "");
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled(ct);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            // NOTE: intentionally NOT cleaning up _persistentWebView here — it stays alive
        });

        return tcs.Task;
    }

    private Task<string> FetchInternalAsync(string url, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            var webView = new WebView
            {
                IsVisible        = true,
                Opacity          = 0.01,
                InputTransparent = true,
                WidthRequest     = 1,
                HeightRequest    = 1,
                Source           = new UrlWebViewSource { Url = url }
            };

#if ANDROID
            webView.HandlerChanged += (s, e) =>
            {
                if (webView.Handler?.PlatformView is global::Android.Webkit.WebView androidWebView)
                {
                    try
                    {
                        var settings = androidWebView.Settings;
                        settings.JavaScriptEnabled = true;
                        settings.DomStorageEnabled = true;
                        settings.DatabaseEnabled = true;
                        settings.SetSupportMultipleWindows(false);
                        settings.JavaScriptCanOpenWindowsAutomatically = false;
                        settings.UserAgentString = "Mozilla/5.0 (Linux; Android 10; SM-G973F) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Mobile Safari/537.36";
                        androidWebView.OnResume();
                        androidWebView.ResumeTimers();
                    }
                    catch { }
                }
            };
#endif

            var (hostLayout, overlay) = AttachWebView(webView);

            using var reg = ct.Register(() =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    tcs.TrySetCanceled(ct);
                    Cleanup(hostLayout, overlay, webView);
                });
            });

            try
            {
                // Give the WebView time to start loading before we poll
                await Task.Delay(1500, ct);

                string? html = null;
                int waited = 0;
                bool timedOut = false;

                while (waited < MaxWaitMs)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(PollMs, ct);
                    waited += PollMs;

#if ANDROID
                    if (webView.Handler?.PlatformView is global::Android.Webkit.WebView awv)
                    {
                        try
                        {
                            awv.OnResume();
                            awv.ResumeTimers();
                        }
                        catch { }
                    }
#endif

                    html = await GetPageHtmlAsync(webView);
                    ct.ThrowIfCancellationRequested();
                    if (html == null || html.Length < 300) continue;

                    // Still on a CF challenge page — keep waiting
                    bool isChallenge =
                        html.Contains("cf-chl-opt") ||
                        html.Contains("cf-browser-verification") ||
                        html.Contains("jschl-answer") ||
                        html.Contains("challenge-form") ||
                        (html.Contains("cloudflare") && html.Contains("checking"));

                    if (!isChallenge) break;

                    if (waited >= MaxWaitMs) timedOut = true;
                }

                // For noveldex.io chapter pages, wait for paragraph/prose content to appear (Next.js hydration)
                if (!timedOut && Regex.IsMatch(url, @"noveldex\.io/series/.+/chapter/\d+", RegexOptions.IgnoreCase))
                {
                    await WaitForNoveldexChapterAsync(webView, ct);
                    ct.ThrowIfCancellationRequested();
                    html = await GetPageHtmlAsync(webView);
                }

                // For noveldex.io series index pages, wait for series title and chapter list to hydrate
                if (!timedOut && url.Contains("noveldex.io/series/") && !url.Contains("/chapter/"))
                {
                    await WaitForNoveldexIndexAsync(webView, ct);
                    ct.ThrowIfCancellationRequested();
                    html = await GetPageHtmlAsync(webView);
                    html = await InjectNoveldexCoverMetaAsync(webView, html);
                }

                // For czbooks index pages, also wait for chapter links to render
                if (!timedOut && url.Contains("czbooks.net/n/") &&
                    !Regex.IsMatch(url, @"czbooks\.net/n/[^/]+/[^/]+"))
                {
                    await WaitForCzBooksChaptersAsync(webView, ct);
                    ct.ThrowIfCancellationRequested();
                    html = await GetPageHtmlAsync(webView);
                }

                // ONLY throw CloudflareExpiredException if real Cloudflare challenge markers are present
                bool hasCfChallenge = html != null && (
                    html.Contains("cf-chl-opt") ||
                    html.Contains("cf-browser-verification") ||
                    (html.Contains("cloudflare") && html.Contains("checking")));

                if (hasCfChallenge)
                {
                    tcs.TrySetException(new Shuka.Core.CloudflareExpiredException(
                        new Uri(url).Host));
                    return;
                }

                if (timedOut)
                {
                    tcs.TrySetException(new TimeoutException($"Page load timed out for {url}"));
                    return;
                }

                tcs.TrySetResult(html ?? "");
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled(ct);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                Cleanup(hostLayout, overlay, webView);
            }
        });

        return tcs.Task;
    }

    /// <summary>
    /// For noveldex.io series index pages (Next.js SPA), polls until the title
    /// and chapter list / total count hydrate in the DOM — up to 25s.
    /// </summary>
    private static async Task WaitForNoveldexIndexAsync(WebView webView, CancellationToken ct)
    {
        const int pollMs  = 600;
        const int maxWait = 25000;
        int waited = 0;

        while (waited < maxWait)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(pollMs, ct);
            waited += pollMs;

#if ANDROID
            if (webView.Handler?.PlatformView is global::Android.Webkit.WebView awv)
            {
                try
                {
                    awv.OnResume();
                    awv.ResumeTimers();
                }
                catch { }
            }
#endif

            // Check if loader is gone and real title/chapters have appeared
            string? js = await webView.EvaluateJavaScriptAsync(
                "(function(){ " +
                "  var loader = document.querySelector('.glitch-loader'); " +
                "  if (loader) return '0'; " +
                "  var h1 = document.querySelector('h1'); " +
                "  var links = document.querySelectorAll('a[href*=\"/chapter/\"]'); " +
                "  if (h1 && (links.length > 0 || (document.body && document.body.innerText.indexOf('Chapters') !== -1))) return '1'; " +
                "  var nextData = document.getElementById('__NEXT_DATA__'); " +
                "  if (nextData && nextData.innerText.length > 500) return '1'; " +
                "  return (document.body && document.body.innerText.length > 800) ? '1' : '0'; " +
                "})()");

            if (js?.Trim('"') == "1")
            {
                await Task.Delay(400, ct);
                return;
            }
        }
    }

    /// <summary>
    /// For noveldex.io chapter pages (Next.js SPA), polls until paragraph/prose content
    /// appears in the DOM — up to 30s. This prevents the download from stalling
    /// on chapters that take longer to hydrate.
    /// </summary>
    private static async Task WaitForNoveldexChapterAsync(WebView webView, CancellationToken ct)
    {
        const int pollMs  = 800;
        const int maxWait = 30000;
        int waited = 0;

        while (waited < maxWait)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(pollMs, ct);
            waited += pollMs;

#if ANDROID
            if (webView.Handler?.PlatformView is global::Android.Webkit.WebView awv)
            {
                try
                {
                    awv.OnResume();
                    awv.ResumeTimers();
                }
                catch { }
            }
#endif

            // Check if the page is locked / paywalled — return immediately if found
            string? lockedJs = await webView.EvaluateJavaScriptAsync(
                "(function(){ " +
                "  var t = (document.body ? document.body.innerText : ''); " +
                "  if (!t) return '0'; " +
                "  return (t.indexOf('Unlock to continue reading') !== -1 || " +
                "          t.indexOf('Sign in to Unlock') !== -1 || " +
                "          t.indexOf('Unlock Chapter') !== -1 || " +
                "          t.indexOf('Unlock this chapter') !== -1 || " +
                "          /\\bunlock\\b.{1,40}\\bcoins?\\b/i.test(t) || " +
                "          /\\bcoins?\\b.{1,40}\\bunlock\\b/i.test(t)) ? '1' : '0'; " +
                "})()");

            if (lockedJs?.Trim('"') == "1")
            {
                return; // Locked chapter — exit poll immediately
            }

            // Check if Next.js hydration completed (glitch-loader gone and story text present)
            string? js = await webView.EvaluateJavaScriptAsync(
                "(function(){ " +
                "  var loader = document.querySelector('.glitch-loader'); " +
                "  if (loader) return '0'; " +
                "  var ps = document.querySelectorAll('p, .prose, article, [class*=\"chapter\"]'); " +
                "  var len = 0; " +
                "  for (var i = 0; i < ps.length; i++) { len += (ps[i].innerText || '').length; } " +
                "  var bodyText = (document.body ? document.body.innerText : ''); " +
                "  if (bodyText.indexOf('LOADING') !== -1 && len < 200) return '0'; " +
                "  return Math.max(len, bodyText.length).toString(); " +
                "})()");

            if (int.TryParse(js?.Trim('"'), out int textLen) && textLen > 200)
            {
                await Task.Delay(400, ct);
                return;
            }
        }
    }

    /// <summary>
    /// For czbooks.net index pages (client-side rendered), polls until
    /// chapter links appear in the DOM — up to 25s.
    /// </summary>
    private static async Task WaitForCzBooksChaptersAsync(WebView webView, CancellationToken ct)
    {
        const int pollMs  = 1000;
        const int maxWait = 25000;
        int waited = 0;

        while (waited < maxWait)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(pollMs, ct);
            waited += pollMs;

#if ANDROID
            if (webView.Handler?.PlatformView is global::Android.Webkit.WebView awv)
            {
                try
                {
                    awv.OnResume();
                    awv.ResumeTimers();
                }
                catch { }
            }
#endif

            string? js = await webView.EvaluateJavaScriptAsync(
                "document.querySelectorAll('a[href*=\"/n/\"]').length.toString()");

            if (int.TryParse(js?.Trim('"'), out int count) && count > 5)
            {
                await Task.Delay(500, ct);
                return;
            }
        }
    }

    /// <summary>
    /// Extracts the real cover CDN URL from the live DOM and injects it as og:image
    /// so NoveldexAdapter can reliably find it (mirrors PlaywrightFetcher on Windows).
    /// </summary>
    private static async Task<string?> InjectNoveldexCoverMetaAsync(WebView webView, string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return html;

        try
        {
            string? coverUrl = await webView.EvaluateJavaScriptAsync(
                """
                (function() {
                    function decodeImg(src) {
                        if (!src) return null;
                        if (src.indexOf('/_next/image') !== -1) {
                            try {
                                var p = new URL(src, location.href).searchParams.get('url');
                                if (p) return p;
                            } catch (e) {}
                        }
                        return src;
                    }
                    var sels = ['[class*="cover" i] img', 'img[alt*="cover" i]'];
                    for (var s = 0; s < sels.length; s++) {
                        var el = document.querySelector(sels[s]);
                        if (el && el.src) {
                            var u = decodeImg(el.src);
                            if (u && u.indexOf('media.noveldex.io') !== -1) return u;
                        }
                    }
                    var nextImgs = document.querySelectorAll('img[src*="/_next/image"]');
                    for (var i = 0; i < nextImgs.length; i++) {
                        try {
                            var urlParam = new URL(nextImgs[i].src, location.href).searchParams.get('url');
                            if (urlParam && urlParam.indexOf('cover') !== -1) return urlParam;
                        } catch (e) {}
                    }
                    for (var j = 0; j < nextImgs.length; j++) {
                        try {
                            var p2 = new URL(nextImgs[j].src, location.href).searchParams.get('url');
                            if (p2 && p2.indexOf('media.noveldex.io') !== -1) return p2;
                        } catch (e) {}
                    }
                    var cdnImgs = document.querySelectorAll('img[src*="media.noveldex.io"]');
                    for (var k = 0; k < cdnImgs.length; k++) {
                        if (cdnImgs[k].src && cdnImgs[k].src.indexOf('cover') !== -1) return cdnImgs[k].src;
                    }
                    if (cdnImgs.length > 0) return cdnImgs[0].src;
                    var og = document.querySelector('meta[property="og:image"]');
                    if (og) {
                        var c = og.getAttribute('content');
                        if (c && c.indexOf('uploads/settings/ogImage') === -1) return c;
                    }
                    return null;
                })()
                """);

            coverUrl = coverUrl?.Trim('"');
            if (string.IsNullOrWhiteSpace(coverUrl) || coverUrl == "null")
                return html;

            string escaped = System.Security.SecurityElement.Escape(coverUrl);
            string injected = $"<meta property=\"og:image\" content=\"{escaped}\" data-shuka-injected=\"1\" />";

            int headIdx = html.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
            int gtIdx   = headIdx >= 0 ? html.IndexOf('>', headIdx) : -1;
            return gtIdx >= 0
                ? html.Insert(gtIdx + 1, injected)
                : injected + html;
        }
        catch
        {
            return html;
        }
    }

    /// <summary>
    /// Extracts the full page HTML without JSON-escaping corruption.
    /// EvaluateJavaScriptAsync wraps the result in a JSON string, which mangles
    /// quotes, backslashes, and Unicode. We base64-encode in JS and decode in C#.
    /// Uses chunked extraction to handle large pages without timing out.
    /// </summary>
    private static async Task<string?> GetPageHtmlAsync(WebView webView)
    {
        // Step 1: store the base64 in a JS global and get the total length safely
        const string initJs = @"
            (function() {
                try {
                    var html = document.documentElement ? document.documentElement.outerHTML : '';
                    if (!html) return '0';
                    var enc = new TextEncoder();
                    var u8 = enc.encode(html);
                    var bin = '';
                    var chunkSize = 1024;
                    for (var i = 0; i < u8.length; i += chunkSize) {
                        var chunk = u8.subarray(i, Math.min(i + chunkSize, u8.length));
                        for (var j = 0; j < chunk.length; j++) {
                            bin += String.fromCharCode(chunk[j]);
                        }
                    }
                    window.__shukaB64 = btoa(bin);
                    return window.__shukaB64.length.toString();
                } catch(e) {
                    try {
                        var html2 = document.documentElement ? document.documentElement.outerHTML : '';
                        window.__shukaB64 = btoa(unescape(encodeURIComponent(html2)));
                        return window.__shukaB64.length.toString();
                    } catch(e2) {
                        window.__shukaB64 = '';
                        return '0';
                    }
                }
            })()";

        string? lenStr = await webView.EvaluateJavaScriptAsync(initJs);
        lenStr = lenStr?.Trim('"');
        if (!int.TryParse(lenStr, out int totalLen) || totalLen == 0) return null;

        // Step 2: read the base64 string in chunks of 50000 chars
        const int chunkSize = 50000;
        var sb = new System.Text.StringBuilder(totalLen);
        int offset = 0;

        while (offset < totalLen)
        {
            int end = Math.Min(offset + chunkSize, totalLen);
            string chunkJs = $"window.__shukaB64.substring({offset},{end})";
            string? chunk = await webView.EvaluateJavaScriptAsync(chunkJs);
            if (chunk == null) break;
            chunk = chunk.Trim('"');
            sb.Append(chunk);
            offset = end;
        }

        // Step 3: clean up global
        await webView.EvaluateJavaScriptAsync("delete window.__shukaB64");

        string b64 = sb.ToString();
        if (string.IsNullOrEmpty(b64)) return null;

        try
        {
            byte[] bytes = Convert.FromBase64String(b64);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Attaches a hidden WebView to the current visible page's layout.
    /// Spans all rows/columns anchored in the bottom-right corner with 1x1 size,
    /// ensuring it never alters or shifts the host page's layout or causes UI jumping.
    /// </summary>
    private static (Layout? hostLayout, Grid? overlay) AttachWebView(WebView webView)
    {
        var overlay = new Grid
        {
            IsVisible         = true,
            Opacity           = 0.01,
            InputTransparent  = true,
            WidthRequest      = 1,
            HeightRequest     = 1,
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions   = LayoutOptions.End,
            ZIndex            = -9999
        };
        overlay.Add(webView);

        var page = GetCurrentPage();
        if (page == null) return (null, null);

        Layout? host = FindAttachableLayout(page);
        if (host != null)
        {
            if (host is Grid)
            {
                Grid.SetRow(overlay, 0);
                Grid.SetColumn(overlay, 0);
                Grid.SetRowSpan(overlay, 99);
                Grid.SetColumnSpan(overlay, 99);
            }
            host.Add(overlay);
            return (host, overlay);
        }

        return (null, null);
    }

    private static void Cleanup(Layout? hostLayout, Grid? overlay, WebView? webView = null)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                if (webView != null)
                {
                    webView.Source = null;
#if ANDROID
                    if (webView.Handler?.PlatformView is global::Android.Webkit.WebView androidWebView)
                    {
                        androidWebView.StopLoading();
                        androidWebView.LoadUrl("about:blank");
                    }
#endif
                    webView.Handler?.DisconnectHandler();
                }

                if (overlay != null)
                {
                    overlay.Clear();
                    if (hostLayout != null && hostLayout.Contains(overlay))
                        hostLayout.Remove(overlay);
                }
            }
            catch { /* ignore cleanup errors */ }
        });
    }

    /// <summary>
    /// Gets the currently visible page, traversing Shell/NavigationPage/TabbedPage wrappers.
    /// </summary>
    private static Page? GetCurrentPage()
    {
        var root = Application.Current?.Windows.FirstOrDefault()?.Page;
        if (root == null) return null;

        return UnwrapPage(root);
    }

    private static Page UnwrapPage(Page page)
    {
        return page switch
        {
            Shell shell           => UnwrapPage(shell.CurrentPage),
            NavigationPage nav    => UnwrapPage(nav.CurrentPage),
            TabbedPage tabbed     => UnwrapPage(tabbed.CurrentPage),
            FlyoutPage flyout     => UnwrapPage(flyout.Detail),
            _                     => page
        };
    }

    /// <summary>
    /// Finds a Layout inside the page that we can safely add a child to.
    /// </summary>
    private static Layout? FindAttachableLayout(Page page)
    {
        if (page is ContentPage cp)
        {
            if (cp.Content is Grid g)    return g;
            if (cp.Content is Layout l)  return l;

            var wrapper = new Grid();
            var existing = cp.Content;
            cp.Content = wrapper;
            if (existing != null) wrapper.Add(existing);
            return wrapper;
        }

        return null;
    }
}
