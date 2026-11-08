using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Shuka.Core;

namespace Shuka.Android.Platform;

/// <summary>
/// Android implementation of ICloudflareBypass.
/// Uses a hidden MAUI WebView to load pages and extract rendered HTML
/// after Cloudflare's JS challenge completes or Next.js SPA hydrates.
/// Handles Shell navigation, tab pages, and direct ContentPages.
///
/// Strategy (in priority order):
///   1. Cookie-first HTTP path: if Android CookieManager already has CF clearance
///      cookies from a prior user session, make a direct HttpClient fetch with those
///      cookies — fast, no WebView spin-up needed.
///   2. Hidden WebView: spin up a background WebView and wait up to 20 s for the CF
///      JS challenge to auto-resolve (works for old-style JS challenges but NOT for
///      CF Managed Challenge / Turnstile which require real user interaction).
///   3. Fail-fast: if the WebView is still showing a CF challenge page at the end
///      of the wait window, throw CloudflareExpiredException immediately so callers
///      (SourceBrowsePage, DownloadManager) can surface a "open in WebView" prompt
///      to the user instead of hanging.
/// </summary>
public class WebViewCloudflareBypass : ICloudflareBypass
{
    // Max time to wait for a hidden-WebView CF challenge to auto-resolve.
    // Lowered from 35 s to 20 s — managed challenges can never auto-resolve so
    // there is no value in waiting longer; old-style JS challenges resolve in <10 s.
    private const int MaxWaitMs   = 20000;
    private const int PollMs      = 1000;

    // Register extended encodings (GBK, Big5, etc.) once
    static WebViewCloudflareBypass()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    // Shared HttpClient for the cookie-first fast path.
    // AllowAutoRedirect = false ensures sensitive Cookie headers are not stripped on redirect.
    private static readonly HttpClient _http = new(
        new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            AllowAutoRedirect = false
        })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

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

            // Try full origin with trailing slash, plain origin, and non-www
            string? cookies = cm.GetCookie($"https://{host}/");
            if (string.IsNullOrWhiteSpace(cookies))
                cookies = cm.GetCookie($"https://{host}");
            if (string.IsNullOrWhiteSpace(cookies) && host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                cookies = cm.GetCookie($"https://{host[4..]}/");

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
        // ── Fast path: use existing CF clearance cookies from CookieManager ──────
        // If the user has previously visited this site in WebView (ShukaQuestPage /
        // WebBrowsePage), Android's CookieManager will hold cf_clearance + other
        // session cookies. Reuse them via a plain HttpClient request — no hidden
        // WebView spin-up, no 20 s wait, no semaphore held.
        try
        {
            string? existingCookies = GetCookiesForUrl(url);
            if (!string.IsNullOrEmpty(existingCookies))
            {
                string? fastHtml = await TryHttpWithCookiesAsync(url, existingCookies, ct);
                if (!string.IsNullOrEmpty(fastHtml) &&
                    fastHtml.Length > 500 &&
                    !IsCloudflareChallenge(fastHtml))
                {
                    return fastHtml;
                }
                // Cookies exist but are expired/invalid — fall through to WebView path
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* ignore fast-path errors; fall through to WebView */ }

        await _sem.WaitAsync(ct);
        try
        {
            // Noveldex chapter pages: reuse a persistent WebView to avoid memory accumulation
            bool isNoveldexChapter = Regex.IsMatch(url,
                @"noveldex\.io/series/.+/chapter/\d+", RegexOptions.IgnoreCase);

            // 69shuba chapter pages: also reuse a persistent WebView.
            // Spinning up a new hidden WebView per chapter (200+ chapters) exhausts Android's
            // WebKit memory and causes all subsequent WebViews to hang for the full 20 s timeout.
            bool is69shubaChapter = Regex.IsMatch(url,
                @"69shuba\.com/txt/\d+/\d+", RegexOptions.IgnoreCase);

            if (isNoveldexChapter || is69shubaChapter)
                return await FetchWithPersistentWebViewAsync(url, ct);

            return await FetchInternalAsync(url, ct);
        }
        finally
        {
            _sem.Release();
        }
    }

    /// <summary>
    /// Returns CF+session cookies for <paramref name="url"/> from Android's CookieManager,
    /// or null if no cookies are stored for that host.
    /// </summary>
    private static string? GetCookiesForUrl(string url)
    {
#if ANDROID
        try
        {
            var cm = global::Android.Webkit.CookieManager.Instance;
            if (cm == null) return null;

            string? cookies = cm.GetCookie(url);
            if (string.IsNullOrWhiteSpace(cookies))
            {
                var uri = new Uri(url);
                cookies = cm.GetCookie($"{uri.Scheme}://{uri.Host}/");
                if (string.IsNullOrWhiteSpace(cookies) && uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                {
                    cookies = cm.GetCookie($"{uri.Scheme}://{uri.Host[4..]}/");
                }
            }

            if (string.IsNullOrWhiteSpace(cookies)) return null;
            return cookies;
        }
        catch { return null; }
#else
        return null;
#endif
    }

    /// <summary>
    /// Makes an HttpClient GET with the given cookie header.
    /// Handles redirects up to 4 hops while preserving the Cookie header.
    /// Returns null on any error or if the response is still a CF challenge.
    /// </summary>
    private static async Task<string?> TryHttpWithCookiesAsync(
        string url, string cookies, CancellationToken ct)
    {
        try
        {
            string currentUrl = url;
            HttpResponseMessage? resp = null;
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            for (int r = 0; r < 4; r++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, currentUrl);
                var uri = new Uri(currentUrl);
                req.Headers.TryAddWithoutValidation("Cookie", cookies);
                req.Headers.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Linux; Android 10; SM-G973F) AppleWebKit/537.36 " +
                    "(KHTML, like Gecko) Chrome/130.0.0.0 Mobile Safari/537.36");
                string referer = $"{uri.Scheme}://{uri.Host}/";
                var bookIdMatch = System.Text.RegularExpressions.Regex.Match(currentUrl, @"/txt/(\d+)/");
                if (bookIdMatch.Success)
                    referer = $"{uri.Scheme}://{uri.Host}/book/{bookIdMatch.Groups[1].Value}/";
                req.Headers.TryAddWithoutValidation("Referer", referer);
                req.Headers.TryAddWithoutValidation("Accept-Language", "zh-TW,zh;q=0.9,zh-CN;q=0.8");
                req.Headers.TryAddWithoutValidation("Accept",
                    "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
                req.Headers.TryAddWithoutValidation("sec-ch-ua",
                    "\"Chromium\";v=\"130\", \"Google Chrome\";v=\"130\", \"Not?A_Brand\";v=\"99\"");
                req.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?1");
                req.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Android\"");
                req.Headers.TryAddWithoutValidation("sec-fetch-dest", "document");
                req.Headers.TryAddWithoutValidation("sec-fetch-mode", "navigate");
                req.Headers.TryAddWithoutValidation("sec-fetch-site", "same-origin");
                req.Headers.TryAddWithoutValidation("sec-fetch-user", "?1");
                req.Headers.TryAddWithoutValidation("upgrade-insecure-requests", "1");

                resp = await _http.SendAsync(req, linked.Token);

                if ((int)resp.StatusCode is 301 or 302 or 303 or 307 or 308 && resp.Headers.Location != null)
                {
                    var nextUri = new Uri(uri, resp.Headers.Location);
                    currentUrl = nextUri.ToString();
                    resp.Dispose();
                    resp = null;
                    continue;
                }
                break;
            }

            if (resp == null || !resp.IsSuccessStatusCode)
            {
                resp?.Dispose();
                return null;
            }

            using (resp)
            {
                byte[] bytes = await resp.Content.ReadAsByteArrayAsync(linked.Token);
                string latin1 = Encoding.Latin1.GetString(bytes);

                // Detect charset: prioritize HTML meta tag for CJK encodings (Cloudflare often injects false UTF-8 header)
                string head = latin1[..Math.Min(latin1.Length, 4096)];
                var cm = System.Text.RegularExpressions.Regex.Match(
                    head, @"charset\s*=\s*[""']?\s*([\w-]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                string charset;
                if (cm.Success && (cm.Groups[1].Value.Equals("gbk", StringComparison.OrdinalIgnoreCase) ||
                                   cm.Groups[1].Value.Equals("gb2312", StringComparison.OrdinalIgnoreCase) ||
                                   cm.Groups[1].Value.Equals("big5", StringComparison.OrdinalIgnoreCase) ||
                                   cm.Groups[1].Value.Equals("gb18030", StringComparison.OrdinalIgnoreCase)))
                {
                    charset = cm.Groups[1].Value.Trim();
                }
                else if (url.Contains("69shuba.com", StringComparison.OrdinalIgnoreCase))
                {
                    charset = "gbk";
                }
                else if (!string.IsNullOrWhiteSpace(resp.Content.Headers.ContentType?.CharSet))
                {
                    charset = resp.Content.Headers.ContentType.CharSet.Trim().Trim('"');
                }
                else if (cm.Success)
                {
                    charset = cm.Groups[1].Value.Trim();
                }
                else
                {
                    charset = "utf-8";
                }

                charset = charset.ToLowerInvariant() switch
                {
                    "gb2312" or "gb_2312" or "csgb2312" or "x-gbk" or "chinese" => "gbk",
                    "big5"   or "csbig5"  or "x-x-big5"                         => "big5",
                    _                                                             => charset
                };

                Encoding enc;
                try   { enc = Encoding.GetEncoding(charset); }
                catch { enc = Encoding.UTF8; }

                string decoded = enc.GetString(bytes);

                // If decoded as UTF-8 but contains replacement characters, retry with GBK
                if (enc == Encoding.UTF8 && decoded.Contains('\uFFFD'))
                {
                    try
                    {
                        var gbkEnc = Encoding.GetEncoding("gbk");
                        string gbkDecoded = gbkEnc.GetString(bytes);
                        if (!gbkDecoded.Contains('\uFFFD'))
                            decoded = gbkDecoded;
                    }
                    catch { }
                }

                return decoded;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>
    /// Navigates the persistent singleton WebView to a new URL and waits for content.
    /// The same WebView instance is reused across chapter fetches for sites that download
    /// many chapters (e.g. Noveldex, 69shuba), so WebKit never accumulates stale DOM trees
    /// from hundreds of separate instances.
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
                        IsVisible         = true,
                        Opacity           = 0.01,
                        InputTransparent  = true,
                        HorizontalOptions = LayoutOptions.Fill,
                        VerticalOptions   = LayoutOptions.Fill,
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
                                awv.Settings.UseWideViewPort    = true;
                                awv.Settings.LoadWithOverviewMode = true;
                                awv.Settings.SetSupportMultipleWindows(false);
                                awv.Settings.JavaScriptCanOpenWindowsAutomatically = false;
                                awv.Settings.UserAgentString    = "Mozilla/5.0 (Linux; Android 10; SM-G973F) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Mobile Safari/537.36";
                                awv.OnResume();
                                awv.ResumeTimers();
                            }
                            catch { }
                        }
                    };
#endif

                    (_persistentHost, _persistentOverlay) = AttachWebView(_persistentWebView);
                }

                // Navigate to the new chapter URL.
                // First navigate to about:blank to flush the previous page's DOM — this
                // prevents GetPageHtmlAsync from returning stale content from the last chapter
                // during the initial settle period before the new page finishes loading.
                _persistentWebView.Source = new UrlWebViewSource { Url = "about:blank" };
                await Task.Delay(300, ct);
                _persistentWebView.Source = new UrlWebViewSource { Url = url };

                using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

                // Parse the target host+path so we can verify the WebView has actually
                // navigated away from about:blank / the previous chapter.
                Uri targetUri;
                try { targetUri = new Uri(url); } catch { targetUri = new Uri("about:blank"); }
                string targetHost = targetUri.Host;

                // Brief settle delay then poll
                await Task.Delay(1500, ct);

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

                    // Guard against stale content: verify the WebView has navigated to
                    // the target host. If the page still shows about:blank or the previous
                    // chapter's domain, skip this poll cycle and keep waiting.
                    string? currentHref = await _persistentWebView.EvaluateJavaScriptAsync(
                        "window.location.href");
                    currentHref = currentHref?.Trim('"');
                    if (!string.IsNullOrEmpty(targetHost) &&
                        !string.IsNullOrEmpty(currentHref) &&
                        !currentHref.Contains(targetHost, StringComparison.OrdinalIgnoreCase))
                    {
                        html = null;
                        continue;
                    }

                    bool isChallenge = IsCloudflareChallenge(html);

                    if (!isChallenge) break;
                }

                // Noveldex-only: wait for Next.js SPA hydration and extract chapter text
                bool isNoveldex = Regex.IsMatch(url,
                    @"noveldex\.io/series/.+/chapter/\d+", RegexOptions.IgnoreCase);
                if (isNoveldex)
                {
                    await WaitForNoveldexChapterAsync(_persistentWebView, ct);
                    ct.ThrowIfCancellationRequested();
                    html = await GetPageHtmlAsync(_persistentWebView);
                    if (string.IsNullOrEmpty(html) || !html.Contains("xorEncryption"))
                    {
                        string? noveldexHtml = await ExtractNoveldexChapterHtmlAsync(_persistentWebView);
                        if (!string.IsNullOrEmpty(noveldexHtml)) html = noveldexHtml;
                    }
                }

                // 69shuba-only: wait for server-rendered .txtnav content to appear.
                // The initial HTML snapshot may load before the chapter text renders
                // (some chapters have a brief JS-driven load delay). Poll for up to 8 s.
                bool is69shuba = url.Contains("69shuba.com", StringComparison.OrdinalIgnoreCase);
                if (is69shuba && (string.IsNullOrEmpty(html) || !html.Contains("txtnav", StringComparison.OrdinalIgnoreCase)))
                {
                    for (int extra = 0; extra < 8; extra++)
                    {
                        ct.ThrowIfCancellationRequested();
                        await Task.Delay(1000, ct);
                        html = await GetPageHtmlAsync(_persistentWebView);
                        if (!string.IsNullOrEmpty(html) && html.Contains("txtnav", StringComparison.OrdinalIgnoreCase))
                            break;
                    }
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
                IsVisible         = true,
                Opacity           = 0.01,
                InputTransparent  = true,
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions   = LayoutOptions.Fill,
                Source            = new UrlWebViewSource { Url = url }
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
                        settings.UseWideViewPort = true;
                        settings.LoadWithOverviewMode = true;
                        settings.SetSupportMultipleWindows(false);
                        settings.JavaScriptCanOpenWindowsAutomatically = false;
                        settings.UserAgentString = "Mozilla/5.0 (Linux; Android 10; SM-G973F) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Mobile Safari/537.36";
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
                    bool isChallenge = IsCloudflareChallenge(html);

                    if (!isChallenge) break;

                    if (waited >= MaxWaitMs) timedOut = true;
                }

                // For noveldex.io chapter pages, wait for paragraph/prose content to appear (Next.js hydration)
                if (!timedOut && Regex.IsMatch(url, @"noveldex\.io/series/.+/chapter/\d+", RegexOptions.IgnoreCase))
                {
                    await WaitForNoveldexChapterAsync(webView, ct);
                    ct.ThrowIfCancellationRequested();
                    html = await GetPageHtmlAsync(webView);
                    if (string.IsNullOrEmpty(html) || !html.Contains("xorEncryption"))
                    {
                        string? noveldexHtml = await ExtractNoveldexChapterHtmlAsync(webView);
                        if (!string.IsNullOrEmpty(noveldexHtml)) html = noveldexHtml;
                    }
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
                bool hasCfChallenge = IsCloudflareChallenge(html);

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

            // Wait for actual story paragraphs from the XOR-decrypted content.
            // Noveldex encrypts chapter text client-side; we poll until >=3 long paragraphs
            // appear inside section[data-chapter-number] (post-React-hydration),
            // not just any body text (which fires early from the unencrypted stats bar).
            string? js = await webView.EvaluateJavaScriptAsync(
                "(function(){ " +
                "  var loader = document.querySelector('.glitch-loader, [class*=\"skeleton\"]'); " +
                "  if (loader) return '0'; " +
                "  var count = 0; " +
                "  var ps = document.querySelectorAll(" +
                "    'section[data-chapter-number] p, " +
                "     [class*=\"reader-content\"] p, " +
                "     [class*=\"chapter-content\"] p, " +
                "     article p, .prose p'" +
                "  ); " +
                "  for (var i = 0; i < ps.length; i++) { " +
                "    var el = ps[i]; " +
                "    if (el.closest('[class*=\"reader-footer\"], [class*=\"reader-stats\"], footer, [data-composer-slot], aside, nav')) continue; " +
                "    var t = (el.innerText || '').trim(); " +
                "    if (t.length > 60) count++; " +
                "  } " +
                "  return count.toString(); " +
                "})()");

            if (int.TryParse(js?.Trim('"'), out int paraCount) && paraCount >= 3)
            {
                await Task.Delay(600, ct);
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
    /// <summary>
    /// Evaluates JS inside the WebView to extract only chapter paragraphs,
    /// wrapping them into &lt;div id=&quot;shuka-extracted&quot;&gt; and completely omitting
    /// page headers, navbars, and footers (RSS, comments, about us).
    /// </summary>
    private static async Task<string?> ExtractNoveldexChapterHtmlAsync(WebView webView)
    {
        try
        {
            // Step 1: execute extraction in JS and encode result to base64 in window.__shukaChapterB64
            const string initJs = @"
                (function() {
                    try {
                        var ps = document.querySelectorAll('section[data-chapter-number] p, [class*=""reader-content""] p, [class*=""chapter-content""] p');
                        if (!ps || ps.length < 3) {
                            ps = document.querySelectorAll('article p, .prose p, p');
                        }
                        var result = [];
                        for (var i = 0; i < ps.length; i++) {
                            var p = ps[i];
                            // Exclude header, footer, nav, stats bar, and comment sections
                            if (p.closest('footer, header, nav, aside, [class*=""site-footer""], [data-composer-slot], [class*=""comment""], [class*=""reader-footer""], [class*=""reader-stats""]'))
                                continue;
                            var t = (p.innerText || '').trim();
                            if (t.length >= 3) {
                                // Filter out boilerplate
                                if (/^(?:JavaScript|Cookies|Privacy|Terms|Copyright|Unlock|Sign in|Please enable|Toggle Menu)/i.test(t) ||
                                    /^(?:First chapter|Last chapter|Next chapter|Previous chapter)$/i.test(t) ||
                                    /(?:NovelDex\s*[-–—]\s*Your Ultimate Destination For the Best Web Novels)/i.test(t) ||
                                    /^(?:Made by Vine|Vine)$/i.test(t) ||
                                    /(?:RSS Feed|Privacy Policy|Terms of Service|Content Takedown Policy|Refund Policy|About Us)/i.test(t) ||
                                    /(?:All Rights Reserved|CULTURAL COMMUNICATION|Join the conversation|Sign in to leave a comment)/i.test(t) ||
                                    /^(?:Home|Series|Collections|Community|Library|Coins Shop|Novels|Comics|Search series)/i.test(t) ||
                                    /^\d{1,3}(?:,\d{3})*\s+words/i.test(t) ||
                                    /^\d{1,3}(?:,\d{3})*\s+views/i.test(t) ||
                                    /^Report\s+Issue/i.test(t) ||
                                    /^Like\s+\d+/i.test(t) ||
                                    /^\d+[\s\d,]*words[\s\d,]*views/i.test(t) ||
                                    t.indexOf('permanent access') !== -1 ||
                                    t.indexOf('Unlocking grants') !== -1 ||
                                    t.indexOf('requestAnimationFrame') !== -1)
                                    continue;
                                result.push(t);
                            }
                        }
                        if (result.length === 0) {
                            window.__shukaChapterB64 = '';
                            return '0';
                        }
                        var json = JSON.stringify(result);
                        var enc = new TextEncoder();
                        var u8 = enc.encode(json);
                        var bin = '';
                        var chunkSize = 1024;
                        for (var i = 0; i < u8.length; i += chunkSize) {
                            var chunk = u8.subarray(i, Math.min(i + chunkSize, u8.length));
                            for (var j = 0; j < chunk.length; j++) {
                                bin += String.fromCharCode(chunk[j]);
                            }
                        }
                        window.__shukaChapterB64 = btoa(bin);
                        return window.__shukaChapterB64.length.toString();
                    } catch(e) {
                        window.__shukaChapterB64 = '';
                        return '0';
                    }
                })()";

            string? lenStr = await webView.EvaluateJavaScriptAsync(initJs);
            lenStr = lenStr?.Trim('"');
            if (!int.TryParse(lenStr, out int totalLen) || totalLen == 0) return null;

            // Step 2: read chunked base64
            const int chunkSize = 50000;
            var sbB64 = new System.Text.StringBuilder(totalLen);
            int offset = 0;
            while (offset < totalLen)
            {
                int end = Math.Min(offset + chunkSize, totalLen);
                string chunkJs = $"window.__shukaChapterB64.substring({offset},{end})";
                string? chunk = await webView.EvaluateJavaScriptAsync(chunkJs);
                if (chunk == null) break;
                chunk = chunk.Trim('"');
                sbB64.Append(chunk);
                offset = end;
            }

            // Step 3: clean up global
            await webView.EvaluateJavaScriptAsync("delete window.__shukaChapterB64");

            string b64 = sbB64.ToString();
            if (string.IsNullOrEmpty(b64)) return null;

            byte[] bytes = Convert.FromBase64String(b64);
            string decodedJson = System.Text.Encoding.UTF8.GetString(bytes);

            var paragraphs = System.Text.Json.JsonSerializer.Deserialize<List<string>>(decodedJson);
            if (paragraphs == null || paragraphs.Count == 0) return null;

            var sb = new System.Text.StringBuilder();
            sb.Append("<html><body><div id=\"shuka-extracted\">");
            foreach (var para in paragraphs)
            {
                string escaped = System.Security.SecurityElement.Escape(para);
                sb.Append("<p>").Append(escaped).Append("</p>");
            }
            sb.Append("</div></body></html>");
            return sb.ToString();
        }
        catch
        {
            return null;
        }
    }
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
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions   = LayoutOptions.Fill,
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

    private static bool IsCloudflareChallenge(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return false;

        // If the page contains real novel content or chapter list, it is NOT a challenge page
        if (html.Length > 2000 &&
            (html.Contains("bookname", StringComparison.OrdinalIgnoreCase) ||
             html.Contains("mybox", StringComparison.OrdinalIgnoreCase) ||
             html.Contains("/txt/", StringComparison.OrdinalIgnoreCase) ||
             html.Contains("yuedu", StringComparison.OrdinalIgnoreCase) ||
             (html.Contains("<h1", StringComparison.OrdinalIgnoreCase) && html.Contains("chapter", StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }

        // True Cloudflare challenge indicators
        return html.Contains("<title>Just a moment", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("<title>Attention Required!", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("id=\"challenge-form\"", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("id=\"challenge-running\"", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("cf-chl-opt", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("cf-browser-verification", StringComparison.OrdinalIgnoreCase) ||
               (html.Contains("cloudflare", StringComparison.OrdinalIgnoreCase) && html.Contains("checking your browser", StringComparison.OrdinalIgnoreCase));
    }
}
