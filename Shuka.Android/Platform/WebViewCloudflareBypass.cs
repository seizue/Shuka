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
    // How long to wait after navigation for CF JS challenge to resolve
    private const int CfWaitMs = 5000;
    // Additional wait for SPA content to load (czbooks uses client-side rendering)
    private const int SpaContentWaitMs = 3000;

    public Task<string> FetchAsync(string url)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                var webView = new WebView
                {
                    IsVisible     = false,
                    WidthRequest  = 1,
                    HeightRequest = 1,
                    Source        = new UrlWebViewSource { Url = url }
                };

                // Find a suitable layout to attach the hidden WebView
                var (hostLayout, overlay) = AttachWebView(webView);

                bool completed = false;

                webView.Navigated += async (s, e) =>
                {
                    if (completed) return;

                    if (e.Result != WebNavigationResult.Success)
                    {
                        completed = true;
                        tcs.TrySetException(new Exception($"WebView navigation failed: {e.Result}"));
                        Cleanup(hostLayout, overlay);
                        return;
                    }

                    // Wait for Cloudflare JS challenge to complete
                    await Task.Delay(CfWaitMs);

                    // Check if we're still on a CF challenge page and wait more if needed
                    try
                    {
                        // Use btoa/blob trick to avoid JS string escaping issues with outerHTML
                        string? checkHtml = await GetPageHtmlAsync(webView);

                        bool stillChallenge = checkHtml != null && (
                            checkHtml.Contains("cf-browser-verification") ||
                            checkHtml.Contains("jschl-answer") ||
                            checkHtml.Contains("challenge-form") ||
                            (checkHtml.Contains("cloudflare") && checkHtml.Contains("checking")));

                        if (stillChallenge)
                        {
                            // Give it more time for CF to resolve
                            await Task.Delay(5000);
                        }

                        // For SPA sites like czbooks.net, wait for dynamic content to render.
                        // Poll until the chapter list links appear (up to 15s).
                        await WaitForContentAsync(webView, url);

                        string? html = await GetPageHtmlAsync(webView);

                        completed = true;
                        tcs.TrySetResult(html ?? "");
                    }
                    catch (Exception ex)
                    {
                        completed = true;
                        tcs.TrySetException(ex);
                    }
                    finally
                    {
                        Cleanup(hostLayout, overlay);
                    }
                };
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return tcs.Task;
    }

    /// <summary>
    /// For SPA sites (czbooks.net uses client-side rendering), polls until
    /// meaningful content appears in the DOM — specifically chapter links.
    /// Falls back after a timeout so we always return something.
    /// </summary>
    private static async Task WaitForContentAsync(WebView webView, string url)
    {
        // Only do SPA polling for czbooks index pages (not chapter pages)
        bool isCzBooksIndex = url.Contains("czbooks.net/n/") &&
                              !Regex.IsMatch(url, @"czbooks\.net/n/[^/]+/[^/]+");

        if (!isCzBooksIndex)
        {
            // For chapter pages, a short extra wait is enough
            await Task.Delay(SpaContentWaitMs);
            return;
        }

        // Poll every 1s for up to 25s waiting for chapter links to appear
        const int pollIntervalMs = 1000;
        const int maxWaitMs = 25000;
        int waited = 0;

        while (waited < maxWaitMs)
        {
            await Task.Delay(pollIntervalMs);
            waited += pollIntervalMs;

            // Count anchor tags that look like chapter links: /n/{bookId}/{chapterId}
            string? js = await webView.EvaluateJavaScriptAsync(
                "document.querySelectorAll('a[href*=\"/n/\"]').length.toString()");

            if (int.TryParse(js?.Trim('"'), out int count) && count > 5)
            {
                // Found enough links — give JS one more tick to finish rendering
                await Task.Delay(500);
                return;
            }
        }
        // Timed out — return whatever we have
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
            IsVisible     = false,
            WidthRequest  = 1,
            HeightRequest = 1
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

    private static void Cleanup(Layout? hostLayout, Grid? overlay)
    {
        if (hostLayout == null || overlay == null) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                if (hostLayout.Contains(overlay))
                    hostLayout.Remove(overlay);
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
