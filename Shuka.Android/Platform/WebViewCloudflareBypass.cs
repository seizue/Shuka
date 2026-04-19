using Shuka.Core;

namespace Shuka.Android.Platform;

/// <summary>
/// Android implementation of ICloudflareBypass.
/// Uses a hidden MAUI WebView to load the page and extract rendered HTML
/// after Cloudflare's JS challenge completes — replacing Playwright on Windows.
/// </summary>
public class WebViewCloudflareBypass : ICloudflareBypass
{
    public Task<string> FetchAsync(string url)
    {
        var tcs = new TaskCompletionSource<string>();

        MainThread.BeginInvokeOnMainThread(() =>
        {
            var webView = new WebView
            {
                IsVisible = false,
                WidthRequest = 1,
                HeightRequest = 1,
                Source = new UrlWebViewSource { Url = url }
            };

            // Get the current page's content layout to attach the hidden WebView
            var currentPage = Application.Current?.Windows[0]?.Page;
            Grid? overlay = null;

            if (currentPage is ContentPage cp && cp.Content is Layout layout)
            {
                overlay = new Grid { IsVisible = false, WidthRequest = 1, HeightRequest = 1 };
                overlay.Add(webView);
                layout.Add(overlay);
            }

            webView.Navigated += async (s, e) =>
            {
                if (e.Result != WebNavigationResult.Success)
                {
                    tcs.TrySetException(new Exception($"WebView navigation failed: {e.Result}"));
                    Cleanup();
                    return;
                }

                // Wait for Cloudflare JS challenge to complete
                await Task.Delay(3500);

                try
                {
                    string? html = await webView.EvaluateJavaScriptAsync(
                        "document.documentElement.outerHTML");
                    tcs.TrySetResult(html ?? "");
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                finally
                {
                    Cleanup();
                }
            };

            void Cleanup()
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (overlay != null &&
                        currentPage is ContentPage cp2 &&
                        cp2.Content is Layout l &&
                        l.Contains(overlay))
                    {
                        l.Remove(overlay);
                    }
                });
            }
        });

        return tcs.Task;
    }
}
