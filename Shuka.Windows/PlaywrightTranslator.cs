using System.Text;
using System.Text.RegularExpressions;
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
/// Chunks of text are passed to https://translate.google.com/?sl=auto&tl=en
/// and the translated result is scraped from the live DOM.
/// </summary>
internal sealed class PlaywrightTranslator : IAsyncDisposable
{
    // Max chars per chunk — translate.google.com accepts up to 5000 chars per request
    private const int ChunkSize = 2000;

    // Regex to detect a significant run of Chinese characters in a translation result.
    private static readonly Regex ChineseRegex =
        new(@"[\u4e00-\u9fff\u3400-\u4dbf\uf900-\ufaff]{10,}", RegexOptions.Compiled);

    // Any Chinese character
    private static readonly Regex AnyChineseRegex =
        new(@"[\u4e00-\u9fff\u3400-\u4dbf\uf900-\ufaff]", RegexOptions.Compiled);

    // We keep ONE persistent page open for translation — avoids spin-up overhead per chunk
    private IPlaywright? _playwright;
    private IBrowser?    _browser;
    private IPage?       _page;

    private readonly SemaphoreSlim _pageLock = new(1, 1);
    private bool _disposed;

    // Counts consecutive failures — used to trigger a page reset
    private int _domFailStreak;

    // ── Lazy browser init ─────────────────────────────────────────────────────

    private async Task EnsurePageAsync(CancellationToken ct)
    {
        if (_page != null) return;
        await _pageLock.WaitAsync(ct);
        try
        {
            if (_page != null) return;
            await CreatePageAsync(ct);
        }
        finally
        {
            _pageLock.Release();
        }
    }

    /// <summary>Creates (or recreates) a fresh browser context + page and primes it.</summary>
    private async Task CreatePageAsync(CancellationToken ct)
    {
        _playwright ??= await Playwright.CreateAsync();

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

        if (_browser == null)
        {
            try
            {
                _browser = await _playwright.Chromium.LaunchAsync(launchOptions);
            }
            catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist") || ex.Message.Contains("install"))
            {
                Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
                _browser = await _playwright.Chromium.LaunchAsync(launchOptions);
            }
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

        // Prime the page
        await _page.GotoAsync("https://translate.google.com/?sl=auto&tl=en&op=translate",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });

        try
        {
            await _page.WaitForSelectorAsync(
                SourceTextareaSelector,
                new PageWaitForSelectorOptions { Timeout = 10000 });
        }
        catch { }

        await Task.Delay(800, ct);
    }

    private async Task ResetPageAsync(CancellationToken ct)
    {
        try { if (_page != null) await _page.CloseAsync(); } catch { }
        _page = null;
        _domFailStreak = 0;
        await CreatePageAsync(ct);
    }

    // Selectors for Google Translate UI
    private const string SourceTextareaSelector =
        "textarea[jsname='yFVVkb'], textarea.er8xn, textarea[aria-label*='Source text'], textarea[aria-label*='source']";

    private const string OutputEnglishSelector =
        "span[jsname='W297wb'], span[lang='en']";

    // ── Public translate entry point ──────────────────────────────────────────

    public async Task<string> Translate(string text, Action<string>? log = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        // Skip translation if text has no Chinese characters (already English/numbers)
        if (!AnyChineseRegex.IsMatch(text))
            return text;

        await EnsurePageAsync(ct);

        var chunks  = SplitIntoChunks(text);
        var results = new string[chunks.Count];

        for (int i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (chunks.Count > 1)
                log?.Invoke($"[Browser Translate] Segment {i + 1}/{chunks.Count}...");

            string? translated = await TranslateChunkAsync(chunks[i], log, ct);
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

    private static bool ContainsChinese(string text) => ChineseRegex.IsMatch(text);

    // ── Single-chunk browser translation ─────────────────────────────────────

    private async Task<string?> TranslateChunkAsync(string chunk, Action<string>? log, CancellationToken ct)
    {
        await _pageLock.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();

            if (_domFailStreak >= 3)
            {
                log?.Invoke("   [PW] Resetting browser page due to repeated failures...");
                await ResetPageAsync(ct);
            }

            // ── Method 1: Fill source textarea ──────────────────────────────────
            try
            {
                if (!_page!.Url.Contains("translate.google.com"))
                {
                    await _page.GotoAsync("https://translate.google.com/?sl=auto&tl=en&op=translate",
                        new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20000 });
                    await Task.Delay(600, ct);
                }

                // Dismiss consent dialog if any
                try
                {
                    var consentBtn = await _page.QuerySelectorAsync(
                        "button:has-text('Accept all'), button:has-text('Reject all'), " +
                        "button:has-text('I agree'), form[action*='consent'] button");
                    if (consentBtn != null)
                    {
                        await consentBtn.ClickAsync();
                        await Task.Delay(800, ct);
                    }
                }
                catch { }

                var sourceArea = await _page.QuerySelectorAsync(SourceTextareaSelector);
                if (sourceArea == null)
                {
                    await _page.GotoAsync("https://translate.google.com/?sl=auto&tl=en&op=translate",
                        new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20000 });
                    await Task.Delay(1000, ct);
                    sourceArea = await _page.QuerySelectorAsync(SourceTextareaSelector);
                }

                if (sourceArea != null)
                {
                    // Focus and clear previous content completely
                    await sourceArea.ClickAsync();
                    await _page.Keyboard.PressAsync("Control+A");
                    await _page.Keyboard.PressAsync("Backspace");
                    await Task.Delay(150, ct);

                    // Insert the new chunk text
                    await _page.Keyboard.InsertTextAsync(chunk);

                    // Wait for new translated text to render
                    string? result = null;
                    try
                    {
                        await _page.WaitForSelectorAsync(
                            OutputEnglishSelector,
                            new PageWaitForSelectorOptions { Timeout = 5000 });

                        result = await TryExtractTranslation(_page, ct);
                    }
                    catch (TimeoutException)
                    {
                        result = await TryExtractTranslation(_page, ct);
                    }

                    // Poll briefly if output not ready or still contains Chinese
                    if (string.IsNullOrWhiteSpace(result) || ContainsChinese(result))
                    {
                        result = null;
                        for (int attempt = 0; attempt < 8; attempt++)
                        {
                            ct.ThrowIfCancellationRequested();
                            await Task.Delay(300, ct);
                            result = await TryExtractTranslation(_page, ct);
                            if (!string.IsNullOrWhiteSpace(result) && !ContainsChinese(result))
                                break;
                            result = null;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(result) && !ContainsChinese(result))
                    {
                        log?.Invoke($"   [PW] UI fill succeeded ({result.Length} chars)");
                        _domFailStreak = 0;
                        await Task.Delay(200, ct);
                        return result.Trim();
                    }

                    log?.Invoke("   [PW] UI fill: output still contains Chinese or is empty");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                log?.Invoke($"   [PW] UI fill error: {ex.Message}");
            }

            // ── Method 2: In-page POST fetch (fallback) ─────────────────────────
            string? inPageResult = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    string? raw = await _page!.EvaluateAsync<string?>(@"
                        async (text) => {
                            try {
                                const url = 'https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=en&dt=t';
                                const res = await fetch(url, {
                                    method: 'POST',
                                    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
                                    body: 'q=' + encodeURIComponent(text)
                                });
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

                    if (!string.IsNullOrWhiteSpace(raw) && !ContainsChinese(raw))
                    {
                        inPageResult = raw;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    log?.Invoke($"   [PW] GTX fetch attempt {attempt + 1}: {ex.Message}");
                }

                if (attempt < 2)
                    await Task.Delay(500 * (attempt + 1), ct);
            }

            if (!string.IsNullOrWhiteSpace(inPageResult))
            {
                log?.Invoke($"   [PW] In-page fetch succeeded ({inPageResult.Length} chars)");
                _domFailStreak = 0;
                return inPageResult.Trim();
            }

            _domFailStreak++;
            log?.Invoke($"   [PW] All methods failed (streak: {_domFailStreak})");
            return null;
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
                    // 1. First try output container with innerText (preserves block newlines in Chromium)
                    const outputContainer =
                        document.querySelector('[data-language-to-translate-into]') ||
                        document.querySelector('div[jsname="wP4q4b"]');
                    if (outputContainer) {
                        const t = (outputContainer.innerText || '').trim();
                        if (t.length > 0) return t;
                    }

                    // 2. Target translated spans (jsname="W297wb") in modern Google Translate
                    const wSpans = document.querySelectorAll('span[jsname="W297wb"]');
                    if (wSpans.length > 0) {
                        const parts = [];
                        for (const el of wSpans) {
                            const t = (el.innerText || el.textContent || '').trim();
                            if (t) parts.push(t);
                        }
                        const joined = parts.join('\n');
                        if (joined.length > 0) return joined;
                    }

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
