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
/// (sl=auto lets Google detect Simplified vs Traditional Chinese automatically)
/// and the translated result is scraped from the live DOM.
/// </summary>
internal sealed class PlaywrightTranslator : IAsyncDisposable
{
    // Max chars per chunk — translate.google.com accepts up to 5000 chars per request
    private const int ChunkSize = 2000;

    // Regex to detect a significant run of Chinese characters in a translation result.
    // If 10+ CJK characters remain the translation is considered failed / untranslated.
    private static readonly Regex ChineseRegex =
        new(@"[\u4e00-\u9fff\u3400-\u4dbf\uf900-\ufaff]{10,}", RegexOptions.Compiled);

    // We keep ONE persistent page open for translation — avoids spin-up overhead per chunk
    private IPlaywright? _playwright;
    private IBrowser?    _browser;
    private IPage?       _page;

    private readonly SemaphoreSlim _pageLock = new(1, 1);
    private bool _disposed;

    // Counts consecutive DOM-fallback failures — used to trigger a page reset
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

        // Prime the page — navigate to the base translate page and wait for the UI to load.
        // We use the base URL (no text) so we can fill the textarea for each chunk.
        // sl=auto: Google auto-detects Simplified vs Traditional Chinese.
        await _page.GotoAsync("https://translate.google.com/?sl=auto&tl=en&op=translate",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });

        // Wait for the source textarea to be present before we start using it
        try
        {
            await _page.WaitForSelectorAsync(
                SourceTextareaSelector,
                new PageWaitForSelectorOptions { Timeout = 10000 });
        }
        catch { /* non-fatal — polling in TranslateChunkAsync will handle it */ }

        await Task.Delay(800, ct);
    }

    /// <summary>
    /// Closes the current page and opens a fresh one.
    /// Called (while _pageLock is already held) after repeated DOM-extraction failures.
    /// </summary>
    private async Task ResetPageAsync(CancellationToken ct)
    {
        try { if (_page != null) await _page.CloseAsync(); } catch { }
        _page = null;
        _domFailStreak = 0;
        await CreatePageAsync(ct);
    }

    // Selectors for the Google Translate UI
    // Source textarea — aria-label varies by locale; jsname is stable across builds
    private const string SourceTextareaSelector =
        "textarea[jsname='yFVVkb'], textarea.er8xn, textarea[aria-label*='Source text'], textarea[aria-label*='source']";

    // Output panel — the container whose attribute specifies the target language
    private const string OutputPanelSelector = "[data-language-to-translate-into]";

    // English span inside the output panel — present once translation has rendered
    private const string OutputEnglishSelector =
        "[data-language-to-translate-into] span[lang='en'], " +
        "[data-language-to-translate-into] span[jsname='W297wb']";

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

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if <paramref name="text"/> still contains a significant run of
    /// Chinese characters — indicating the translation was silently skipped or failed.
    /// </summary>
    private static bool ContainsChinese(string text) => ChineseRegex.IsMatch(text);

    // ── Single-chunk browser translation ─────────────────────────────────────

    private async Task<string?> TranslateChunkAsync(string chunk, Action<string>? log, CancellationToken ct)
    {
        await _pageLock.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();

            // Reset page if it has been consistently failing
            if (_domFailStreak >= 3)
            {
                log?.Invoke("   [PW] Resetting browser page due to repeated failures...");
                await ResetPageAsync(ct);
            }

            // ── Method 1: Fill the source textarea and wait for the English output ──────
            //
            // This mimics real user interaction (type text → wait for translation).
            // It avoids all URL-length limits and the GTX API rate-limiting.
            // The page stays on translate.google.com between chunks — only the text changes.
            try
            {
                // Make sure we are on the translate page (handle any redirects / reloads)
                if (!_page!.Url.Contains("translate.google.com"))
                {
                    await _page.GotoAsync("https://translate.google.com/?sl=auto&tl=en&op=translate",
                        new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20000 });
                    await Task.Delay(600, ct);
                }

                // Handle cookie / consent dialog that sometimes appears
                try
                {
                    var consentBtn = await _page.QuerySelectorAsync(
                        "button:has-text('Accept all'), button:has-text('Reject all'), " +
                        "button:has-text('I agree'), form[action*='consent'] button");
                    if (consentBtn != null)
                    {
                        log?.Invoke("   [PW] Dismissing consent dialog...");
                        await consentBtn.ClickAsync();
                        await Task.Delay(800, ct);
                    }
                }
                catch { }

                // Locate the source textarea
                var sourceArea = await _page.QuerySelectorAsync(SourceTextareaSelector);
                if (sourceArea == null)
                {
                    // Re-navigate and try once more
                    await _page.GotoAsync("https://translate.google.com/?sl=auto&tl=en&op=translate",
                        new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20000 });
                    await Task.Delay(1000, ct);
                    sourceArea = await _page.QuerySelectorAsync(SourceTextareaSelector);
                }

                if (sourceArea != null)
                {
                    // Clear any previous text and fill with the new chunk
                    await sourceArea.ClickAsync();
                    await _page.Keyboard.PressAsync("Control+A");
                    await sourceArea.FillAsync(chunk);

                    // Wait for the English translation output to appear.
                    // WaitForSelectorAsync polls the DOM — far more reliable than a fixed-delay loop.
                    string? result = null;
                    try
                    {
                        await _page.WaitForSelectorAsync(
                            OutputEnglishSelector,
                            new PageWaitForSelectorOptions { Timeout = 15000 });

                        result = await TryExtractTranslation(_page, ct);
                    }
                    catch (TimeoutException)
                    {
                        log?.Invoke("   [PW] WaitForSelector timed out — trying extract anyway");
                        result = await TryExtractTranslation(_page, ct);
                    }

                    // If the initial wait gave something but it still has Chinese, poll a bit longer
                    if (string.IsNullOrWhiteSpace(result) || ContainsChinese(result))
                    {
                        result = null;
                        for (int attempt = 0; attempt < 12; attempt++)
                        {
                            ct.ThrowIfCancellationRequested();
                            await Task.Delay(500, ct);
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
                        // Small inter-chunk pause so we don't look like a bot hammering the UI
                        await Task.Delay(300, ct);
                        return result.Trim();
                    }

                    log?.Invoke("   [PW] UI fill: output still contains Chinese or is empty");
                }
                else
                {
                    log?.Invoke("   [PW] Source textarea not found");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                log?.Invoke($"   [PW] UI fill error: {ex.Message}");
            }

            // ── Method 2: In-page GTX fetch (fallback) ───────────────────────────────
            //
            // Called only when the UI interaction above failed.
            // sl=auto, 3 retries with backoff.
            string? inPageResult = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    string? raw = await _page!.EvaluateAsync<string?>(@"
                        async (text) => {
                            try {
                                const url = 'https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=en&dt=t&q=' + encodeURIComponent(text);
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
                    await Task.Delay(800 * (attempt + 1), ct);
            }

            if (!string.IsNullOrWhiteSpace(inPageResult))
            {
                log?.Invoke($"   [PW] GTX fetch succeeded ({inPageResult.Length} chars)");
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
                    // ── Output-panel-only selectors ───────────────────────────────────────
                    // We must only read the TARGET (English) side, never the source panel.

                    // 1. Output container has data-language-to-translate-into attribute.
                    //    Prefer span[lang="en"] children — these are the translated segments
                    //    and are never present in the Chinese source panel.
                    const outputContainer =
                        document.querySelector('[data-language-to-translate-into]') ||
                        document.querySelector('div[jsname="wP4q4b"]');

                    if (outputContainer) {
                        const enSpans = outputContainer.querySelectorAll('span[lang="en"]');
                        if (enSpans.length > 0) {
                            const parts = [];
                            for (const el of enSpans) {
                                const t = (el.innerText || el.textContent || '').trim();
                                if (t) parts.push(t);
                            }
                            const joined = parts.join('\n');
                            if (joined.length > 0) return joined;
                        }

                        // Fast-path: read the output container's visible text directly
                        const containerText = (outputContainer.innerText || outputContainer.textContent || '').trim();
                        if (containerText.length > 0) return containerText;
                    }

                    // 2. Named output spans used by current Google Translate builds.
                    //    jsname="W297wb" = individual translated sentence spans.
                    //    jsname="jqKxS"  = translated paragraph wrapper.
                    const namedSelectors = [
                        'span[jsname="W297wb"]',
                        'span[jsname="jqKxS"]',
                    ];
                    for (const sel of namedSelectors) {
                        const els = document.querySelectorAll(sel);
                        if (els.length > 0) {
                            const parts = [];
                            for (const el of els) {
                                const t = (el.innerText || el.textContent || '').trim();
                                if (t) parts.push(t);
                            }
                            const joined = parts.join(' ');
                            if (joined.length > 0) return joined;
                        }
                    }

                    // 3. c-wiz translation output — pick ONLY spans marked lang="en".
                    //    This avoids accidentally reading the Chinese input-panel spans.
                    const enOnly = document.querySelectorAll('c-wiz span[lang="en"]');
                    if (enOnly.length > 0) {
                        const parts = [];
                        for (const el of enOnly) {
                            const t = (el.innerText || el.textContent || '').trim();
                            if (t) parts.push(t);
                        }
                        const joined = parts.join(' ');
                        if (joined.length > 0) return joined;
                    }

                    // 4. Textarea fallback (older Google Translate layout)
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
