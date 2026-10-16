using System.Text;
using Microsoft.Playwright;

namespace Shuka;

/// <summary>
/// Translates Chinese text to English using Google Translate loaded inside a real
/// Chromium browser (via the Playwright engine already bundled with Shuka Windows).
///
/// Because Chromium renders the full page with a real TLS fingerprint and proper
/// browser headers, Google does NOT rate-limit or block it the way it blocks raw
/// HttpClient or curl requests (HTTP 429 / CAPTCHA redirect).
///
/// Chunks of text are passed to https://translate.google.com/?sl=zh-CN&tl=en
/// and the translated result is scraped from the live DOM.
/// </summary>
internal sealed class PlaywrightTranslator : IAsyncDisposable
{
    // Max chars per chunk — translate.google.com accepts up to 5000 chars per request
    private const int ChunkSize = 2000;

    // We keep ONE persistent page open for translation — avoids spin-up overhead per chunk
    private IPlaywright? _playwright;
    private IBrowser?    _browser;
    private IPage?       _page;

    private readonly SemaphoreSlim _pageLock = new(1, 1);
    private bool _disposed;

    // ── Lazy browser init ─────────────────────────────────────────────────────

    private async Task EnsurePageAsync(CancellationToken ct)
    {
        if (_page != null) return;
        await _pageLock.WaitAsync(ct);
        try
        {
            if (_page != null) return;

            _playwright = await Playwright.CreateAsync();
            var launchOptions = new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args     = new[]
                {
                    "--disable-blink-features=AutomationControlled",
                    "--no-sandbox",
                    "--disable-web-security",
                    "--disable-dev-shm-usage",
                }
            };

            try
            {
                _browser = await _playwright.Chromium.LaunchAsync(launchOptions);
            }
            catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist") || ex.Message.Contains("install"))
            {
                Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
                _browser = await _playwright.Chromium.LaunchAsync(launchOptions);
            }

            var ctx = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                            "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
                Locale              = "en-US",
                JavaScriptEnabled   = true,
                IgnoreHTTPSErrors   = true,
            });

            // Spoof navigator.webdriver to reduce bot detection risk
            await ctx.AddInitScriptAsync(
                "Object.defineProperty(navigator,'webdriver',{get:()=>undefined});");

            _page = await ctx.NewPageAsync();

            // Prime the page — navigate once so cookies / session are set
            await _page.GotoAsync("https://translate.google.com/?sl=zh-CN&tl=en",
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });
            await Task.Delay(1000, ct);
        }
        finally
        {
            _pageLock.Release();
        }
    }

    // ── Public translate entry point ──────────────────────────────────────────

    public async Task<string> Translate(string text, Action<string>? log = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        await EnsurePageAsync(ct);

        var chunks  = SplitIntoChunks(text);
        var results = new string[chunks.Count];

        for (int i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (chunks.Count > 1)
                log?.Invoke($"[Browser Translate] Segment {i + 1}/{chunks.Count}...");

            string? translated = await TranslateChunkAsync(chunks[i], log, ct);

            // If browser translate fails for a chunk, keep original for that chunk only
            results[i] = string.IsNullOrWhiteSpace(translated) ? chunks[i] : translated;
        }

        return string.Join("\n", results);
    }

    // ── Chunking ──────────────────────────────────────────────────────────────

    private static List<string> SplitIntoChunks(string text)
    {
        var chunks = new List<string>();
        var cur    = new StringBuilder(ChunkSize + 200);

        foreach (string line in text.Split('\n'))
        {
            if (cur.Length + line.Length + 1 > ChunkSize && cur.Length > 0)
            {
                chunks.Add(cur.ToString());
                cur.Clear();
            }
            if (cur.Length > 0) cur.Append('\n');
            cur.Append(line);
        }
        if (cur.Length > 0) chunks.Add(cur.ToString());
        return chunks;
    }

    // ── Single-chunk browser translation ─────────────────────────────────────

    private async Task<string?> TranslateChunkAsync(string chunk, Action<string>? log, CancellationToken ct)
    {
        await _pageLock.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();

            // Method 1: Execute fetch from within the Chromium browser context.
            // Because this runs inside Chromium with real browser TLS/cookies/session,
            // Google does not 429 rate-limit it.
            try
            {
                string? inPageResult = await _page!.EvaluateAsync<string?>(@"
                    async (text) => {
                        try {
                            const url = 'https://translate.googleapis.com/translate_a/single?client=gtx&sl=zh-CN&tl=en&dt=t&q=' + encodeURIComponent(text);
                            const res = await fetch(url);
                            if (!res.ok) return null;
                            const data = await res.json();
                            if (Array.isArray(data) && Array.isArray(data[0])) {
                                return data[0].map(item => item[0]).filter(Boolean).join('');
                            }
                            return null;
                        } catch (e) {
                            return null;
                        }
                    }", chunk);

                if (!string.IsNullOrWhiteSpace(inPageResult) && inPageResult.Trim() != chunk.Trim())
                {
                    log?.Invoke($"   [PW] In-page fetch succeeded ({inPageResult.Length} chars)");
                    return inPageResult.Trim();
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"   [PW] In-page fetch note: {ex.Message}");
            }

            // Method 2: DOM navigation fallback
            string encoded = Uri.EscapeDataString(chunk);
            string url = $"https://translate.google.com/?sl=zh-CN&tl=en&text={encoded}&op=translate";

            await _page!.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout   = 20000
            });

            // Handle potential cookie consent dialogs (common in EU / clean browser profiles)
            try
            {
                var consentBtn = await _page.QuerySelectorAsync("button:has-text('Accept all'), button:has-text('Reject all'), button:has-text('I agree'), form[action*='consent'] button");
                if (consentBtn != null)
                {
                    log?.Invoke("   [PW] Dismissing Google consent dialog...");
                    await consentBtn.ClickAsync();
                    await Task.Delay(1000, ct);
                }
            }
            catch { }

            string? result = null;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(500, ct);

                result = await TryExtractTranslation(_page, ct);
                if (!string.IsNullOrWhiteSpace(result) && result.Trim() != chunk.Trim())
                    break;
                result = null;
            }

            return result?.Trim();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log?.Invoke($"   [PW] Error: {ex.Message}");
            return null;
        }
        finally
        {
            _pageLock.Release();
        }
    }

    private static async Task<string?> TryExtractTranslation(IPage page, CancellationToken ct)
    {
        try
        {
            string? r = await page.EvaluateAsync<string?>(
                """
                () => {
                    // Modern Google Translate output spans
                    const selectors = [
                        'span[jsname="W297wb"]',
                        'span[jsname="jqKxS"]',
                        '[data-language-to-translate-into] span',
                        'div[jsname="wP4q4b"]',
                        'c-wiz[data-source-language] span[lang]'
                    ];

                    for (const sel of selectors) {
                        const els = document.querySelectorAll(sel);
                        if (els.length > 0) {
                            const parts = [];
                            for (const el of els) {
                                const t = (el.innerText || el.textContent || '').trim();
                                if (t) parts.push(t);
                            }
                            if (parts.length > 0) return parts.join(' ');
                        }
                    }

                    // Textarea fallback
                    const ta = document.querySelector('textarea[aria-label*="Translation"]') ||
                               document.querySelector('textarea[aria-label*="translation"]');
                    if (ta && ta.value && ta.value.trim()) return ta.value.trim();

                    return null;
                }
                """);
            if (!string.IsNullOrWhiteSpace(r)) return r;
        }
        catch { }

        return null;
    }

    // ── Disposal ──────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (_page   != null) await _page.CloseAsync();   } catch { }
        try { if (_browser != null) await _browser.CloseAsync(); } catch { }
        _playwright?.Dispose();
        _pageLock.Dispose();
    }
}
