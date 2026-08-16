using System.Text.RegularExpressions;
using Shuka.Core;

namespace Shuka.Android.Platform;

/// <summary>
/// Android implementation of ICloudflareBypass.
/// Uses a hidden MAUI WebView to load the page and extract rendered HTML
/// after Cloudflare's JS challenge completes.
/// Handles Shell navigation, tab pages, and direct ContentPages.
/// </summary>
public class WebViewCloudflareBypass : ICloudflareBypass
{
    // Max time to wait for CF challenge + real page to load
    private const int MaxWaitMs   = 35000;
    private const int PollMs      = 1500;

    // Serialize all WebView fetches — CF cookie must be established before
    // the next fetch starts. Multiple concurrent WebViews cause CF to re-challenge.
    private static readonly SemaphoreSlim _sem = new(1, 1);

    public async Task<string> FetchAsync(string url, CancellationToken ct = default)
    {
        await _sem.WaitAsync(ct);
        try
        {
            return await FetchInternalAsync(url, ct);
        }
        finally
        {
            _sem.Release();
        }
    }

    private Task<string> FetchInternalAsync(string url, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            var webView = new WebView
            {
                // Keep visible in the render tree so Android doesn't throttle JS execution.
                // Opacity near-zero makes it invisible to the user while staying active.
                IsVisible      = true,
                Opacity        = 0.01,
                InputTransparent = true,
                WidthRequest   = 1,
                HeightRequest  = 1,
                Source         = new UrlWebViewSource { Url = url }
            };

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
                await Task.Delay(2000, ct);

                string? html = null;
                int waited = 0;
                bool timedOut = false;

                while (waited < MaxWaitMs)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(PollMs, ct);
                    waited += PollMs;

                    html = await GetPageHtmlAsync(webView);
                    ct.ThrowIfCancellationRequested();
                    if (html == null || html.Length < 500) continue;

                    // Still on a CF challenge page — keep waiting
                    bool isChallenge =
                        html.Contains("cf-chl-opt") ||
                        html.Contains("cf-browser-verification") ||
                        html.Contains("jschl-answer") ||
                        html.Contains("challenge-form") ||
                        (html.Contains("cloudflare") && html.Contains("checking"));

                    if (!isChallenge) break;

                    // If we've exhausted the wait time, flag it
                    if (waited >= MaxWaitMs) timedOut = true;
                }

                // For noveldex.io chapter pages, wait for paragraph content to appear (Next.js hydration)
                if (!timedOut && Regex.IsMatch(url, @"noveldex\.io/series/.+/chapter/\d+", RegexOptions.IgnoreCase))
                {
                    await WaitForNoveldexChapterAsync(webView, ct);
                    ct.ThrowIfCancellationRequested();
                    html = await GetPageHtmlAsync(webView);
                }

                // For czbooks index pages, also wait for chapter links to render
                if (!timedOut && url.Contains("czbooks.net/n/") &&
                    !Regex.IsMatch(url, @"czbooks\.net/n/[^/]+/[^/]+"))
                {
                    await WaitForCzBooksChaptersAsync(webView, ct);
                    ct.ThrowIfCancellationRequested();
                    html = await GetPageHtmlAsync(webView);
                }

                // If we timed out still on a challenge page, the cookie has expired
                if (timedOut || (html != null && (
                    html.Contains("cf-chl-opt") ||
                    html.Contains("cf-browser-verification") ||
                    (html.Contains("cloudflare") && html.Contains("checking")))))
                {
                    tcs.TrySetException(new Shuka.Core.CloudflareExpiredException(
                        new Uri(url).Host));
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
    /// For noveldex.io chapter pages (Next.js SPA), polls until paragraph content
    /// appears in the DOM — up to 30s. This prevents the download from stalling
    /// on chapters that take longer to hydrate.
    /// </summary>
    private static async Task WaitForNoveldexChapterAsync(WebView webView, CancellationToken ct)
    {
        const int pollMs  = 1200;
        const int maxWait = 30000;
        int waited = 0;

        while (waited < maxWait)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(pollMs, ct);
            waited += pollMs;

            // Check if the page is locked / paywalled — return immediately if found
            string? lockedJs = await webView.EvaluateJavaScriptAsync(
                "(function(){ " +
                "  var t = (document.body ? document.body.innerText : ''); " +
                "  return (t.indexOf('Unlock to continue reading') !== -1 || " +
                "          t.indexOf('Sign in to Unlock') !== -1 || " +
                "          t.indexOf('Unlock Chapter') !== -1 || " +
                "          t.indexOf('Unlock this chapter') !== -1 || " +
                "          (t.indexOf('coins') !== -1 && t.indexOf('Unlock') !== -1)) ? '1' : '0'; " +
                "})()");

            if (lockedJs?.Trim('"') == "1")
            {
                return; // Locked chapter — exit poll immediately
            }

            // Check if any paragraph content has appeared (chapter text)
            string? js = await webView.EvaluateJavaScriptAsync(
                "(function(){ var ps = document.querySelectorAll('p'); var len = 0; " +
                "for(var i=0;i<ps.length;i++){ len += ps[i].innerText.length; } return len.toString(); })()");

            if (int.TryParse(js?.Trim('"'), out int textLen) && textLen > 200)
            {
                await Task.Delay(800, ct);
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
    /// Extracts the full page HTML without JSON-escaping corruption.
    /// EvaluateJavaScriptAsync wraps the result in a JSON string, which mangles
    /// quotes, backslashes, and Unicode. We base64-encode in JS and decode in C#.
    /// Uses chunked extraction to handle large pages without timing out.
    /// </summary>
    private static async Task<string?> GetPageHtmlAsync(WebView webView)
    {
        // Step 1: store the base64 in a JS global and get the total length
        const string initJs = @"
            (function() {
                try {
                    var html = document.documentElement.outerHTML;
                    var bytes = new TextEncoder().encode(html);
                    // Use Uint8Array + apply trick for fast binary string
                    var chunkSize = 8192;
                    var binary = '';
                    for (var i = 0; i < bytes.length; i += chunkSize) {
                        binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunkSize));
                    }
                    window.__shukaB64 = btoa(binary);
                    return window.__shukaB64.length.toString();
                } catch(e) {
                    window.__shukaB64 = '';
                    return '0';
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
    /// Returns the host layout and the overlay Grid that was added.
    /// </summary>
    private static (Layout? hostLayout, Grid? overlay) AttachWebView(WebView webView)
    {
        var overlay = new Grid
        {
            // Keep overlay visible so Android allocates a rendering surface for the WebView.
            // Near-zero opacity keeps it completely invisible to the user.
            IsVisible        = true,
            Opacity          = 0.01,
            InputTransparent = true,
            WidthRequest     = 1,
            HeightRequest    = 1
        };
        overlay.Add(webView);

        // Walk the page hierarchy to find a Layout we can attach to
        var page = GetCurrentPage();
        if (page == null) return (null, null);

        Layout? host = FindAttachableLayout(page);
        if (host != null)
        {
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
                        androidWebView.ClearHistory();
                        androidWebView.Destroy();
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
            // Prefer a Grid or AbsoluteLayout at the root so overlay doesn't affect layout
            if (cp.Content is Grid g)    return g;
            if (cp.Content is Layout l)  return l;

            // Wrap the existing content in a Grid if needed
            var wrapper = new Grid();
            var existing = cp.Content;
            cp.Content = wrapper;
            if (existing != null) wrapper.Add(existing);
            return wrapper;
        }

        return null;
    }
}
