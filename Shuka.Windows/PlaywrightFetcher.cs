using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Shuka.Core;

namespace Shuka;

/// <summary>
/// Windows Playwright-based ICloudflareBypass + general HTTP fetcher.
/// Reuses a single persistent browser context across all fetches for performance.
/// CF clearance cookies are stored in %LocalAppData%\Shuka\browser-profile and
/// survive between runs — after one successful --solve-cf the headless fetches
/// work without retries.
/// </summary>
internal sealed class PlaywrightFetcher : ICloudflareBypass, IAsyncDisposable
{
    private readonly HttpClient _site;
    private readonly SemaphoreSlim _sem = new(1, 1);

    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    private IBrowserContext? _noveldexContext;  // visible (non-headless) context for noveldex
    private IPage? _noveldexWarmPage;           // kept open to maintain session activity

    public PlaywrightFetcher(HttpClient siteClient)
    {
        _site = siteClient;
    }

    // ── Public fetch entry point ──────────────────────────────────────────────

    /// <summary>
    /// Fetches a URL. For JS-rendered sites goes straight to Playwright;
    /// for other sites tries plain HTTP first and falls back on CF detection.
    /// </summary>
    public async Task<string> FetchAsync(string url, int retries = 4)
    {
        // Known JS-rendered sites — skip HTTP entirely and use Playwright
        if (url.Contains("69shuba.com",  StringComparison.OrdinalIgnoreCase) ||
            url.Contains("noveldex.io",  StringComparison.OrdinalIgnoreCase))
            return await FetchWithPlaywrightVerified(url);

        int delay = 1000;
        Exception? last = null;
        for (int i = 0; i <= retries; i++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                var uri = new Uri(url);
                req.Headers.Add("Referer", $"{uri.Scheme}://{uri.Host}/");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var resp = await _site.SendAsync(req, cts.Token);

                bool isCf = resp.Headers.Contains("cf-ray") ||
                            resp.Headers.Server.ToString().Contains("cloudflare");
                if (isCf && ((int)resp.StatusCode is 403 or 503))
                    return await FetchWithPlaywrightVerified(url);

                resp.EnsureSuccessStatusCode();
                byte[] bytes = await resp.Content.ReadAsByteArrayAsync();
                string latin1 = Encoding.Latin1.GetString(bytes);

                if (latin1.Contains("cf-chl-opt", StringComparison.OrdinalIgnoreCase))
                    return await FetchWithPlaywrightVerified(url);

                string charset = DetectCharset(resp, latin1);
                Encoding enc;
                try   { enc = Encoding.GetEncoding(charset); }
                catch { enc = Encoding.UTF8; }
                string result = enc.GetString(bytes);

                if (result.Contains("cf-chl-opt", StringComparison.OrdinalIgnoreCase) ||
                    (result.Contains("请稍候", StringComparison.OrdinalIgnoreCase) &&
                     result.Contains("cloudflare", StringComparison.OrdinalIgnoreCase)))
                    return await FetchWithPlaywrightVerified(url);

                return result;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(delay);
                delay = Math.Min(delay * 2, 16000);
            }
        }
        throw new Exception($"Fetch failed: {url} — {last?.Message}");
    }

    // ICloudflareBypass — used by Shuka.Core's HttpFetcher on Android;
    // on Windows we call FetchWithPlaywrightVerified directly.
    Task<string> ICloudflareBypass.FetchAsync(string url, CancellationToken ct) =>
        FetchWithPlaywrightVerified(url, ct: ct);

    // ── Playwright verified fetch ─────────────────────────────────────────────

    private async Task<string> FetchWithPlaywrightVerified(string url, int maxAttempts = 3, CancellationToken ct = default)
    {
        await _sem.WaitAsync(ct);
        try
        {
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                string html = await FetchWithPlaywright(url, ct);

                bool isChallenge =
                    html.Contains("cf-chl-opt", StringComparison.OrdinalIgnoreCase) ||
                    (html.Contains("请稍候", StringComparison.OrdinalIgnoreCase) &&
                     html.Contains("cloudflare", StringComparison.OrdinalIgnoreCase)) ||
                    (html.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) &&
                     html.Contains("cloudflare", StringComparison.OrdinalIgnoreCase));

                if (!isChallenge) return html;

                if (attempt < maxAttempts)
                {
                    Console.Write($" [CF retry {attempt}/{maxAttempts}, waiting {attempt * 4}s]");
                    await Task.Delay(attempt * 4000, ct);
                }
            }
            return await FetchWithPlaywright(url, ct);
        }
        finally
        {
            _sem.Release();
        }
    }

    private async Task<string> FetchWithPlaywright(string url, CancellationToken ct = default)
    {
        await EnsureBrowserAsync();

        // noveldex.io chapter pages encrypt content and detect headless Chromium.
        // Try headless first, fall back to visible context if content doesn't load.
        if (IsNoveldexUrl(url))
        {
            // Try headless first (reuses main context)
            string html = await FetchWithNoveldexStrategy(url, _context!, ct, useHeadless: true);
            // If headless failed to get real content, try visible context as fallback
            if (IsNoveldexChapterUrl(url) && html.Length < 1000)
            {
                Console.Write("[noveldex] Headless failed, trying visible...");
                html = await FetchWithNoveldexStrategy(url, await EnsureNoveldexContextAsync(), ct, useHeadless: false);
            }
            return html;
        }

        // Non-noveldex sites use standard headless context
        var page = await _context!.NewPageAsync();
        try
        {
            ct.ThrowIfCancellationRequested();
            await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.Load, Timeout = 45000 });

            // Wait for CF/bot challenges to clear
            var deadline = DateTime.UtcNow.AddSeconds(40);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                string t = await page.TitleAsync();
                bool isChallenge =
                    t.Contains("Just a moment") || t.Contains("Checking your browser") ||
                    t.Contains("Please Wait")   || t.Contains("Security Check") ||
                    t.Contains("请稍候")         || t.Contains("验证") ||
                    string.IsNullOrWhiteSpace(t);
                if (!isChallenge) break;
                Console.Write(".");
                await Task.Delay(1000, ct);
            }

            ct.ThrowIfCancellationRequested();
            await Task.Delay(1500, ct);
            return await page.ContentAsync();
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private async Task<string> FetchWithNoveldexStrategy(string url, IBrowserContext ctx, CancellationToken ct, bool useHeadless)
    {
        // For noveldex, reuse the warm page if available to avoid creating 145+ tabs
        IPage page = useHeadless ? await ctx.NewPageAsync() : _noveldexWarmPage ?? await ctx.NewPageAsync();
        bool shouldClose = (page == _noveldexWarmPage) ? false : true;

        try
        {
            ct.ThrowIfCancellationRequested();
            await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.Load, Timeout = 45000 });

            // Wait for CF/bot challenges to clear
            var deadline = DateTime.UtcNow.AddSeconds(40);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                string t = await page.TitleAsync();
                bool isChallenge =
                    t.Contains("Just a moment") || t.Contains("Checking your browser") ||
                    t.Contains("Please Wait")   || t.Contains("Security Check") ||
                    t.Contains("请稍候")         || t.Contains("验证") ||
                    string.IsNullOrWhiteSpace(t);
                if (!isChallenge) break;
                Console.Write(".");
                await Task.Delay(1000, ct);
            }

            ct.ThrowIfCancellationRequested();

            string extracted = await WaitForNoveldexHydrationAsync(page, url, ct);
            // For chapter pages we return the extracted content directly —
            // avoids noscript blocks that ContentAsync includes verbatim.
            if (!string.IsNullOrEmpty(extracted)) return extracted;

            return await page.ContentAsync();
        }
        finally
        {
            if (shouldClose) await page.CloseAsync();
        }
    }

    private static bool IsNoveldexUrl(string url) =>
        url.Contains("noveldex.io", StringComparison.OrdinalIgnoreCase);

    private static bool IsNoveldexChapterUrl(string url) =>
        url.Contains("noveldex.io", StringComparison.OrdinalIgnoreCase) &&
        url.Contains("/chapter/",   StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns (creating if needed) a non-headless browser context for noveldex.io.
    /// The window is minimized so it doesn't distract the user.
    /// Shares the same persistent profile as the headless context so any saved
    /// login session is reused.
    /// </summary>
    private async Task<IBrowserContext> EnsureNoveldexContextAsync()
    {
        if (_noveldexContext != null) return _noveldexContext;

        await EnsureBrowserAsync(); // ensure _playwright is initialised

        string userDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Shuka", "browser-profile-noveldex");
        Directory.CreateDirectory(userDataDir);

        _noveldexContext = await _playwright!.Chromium.LaunchPersistentContextAsync(userDataDir, new()
        {
            Headless = false,
            Args     = ["--disable-blink-features=AutomationControlled", "--no-sandbox"],
            UserAgent         = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
            ViewportSize      = new() { Width = 1280, Height = 800 },
            JavaScriptEnabled = true,
            IgnoreHTTPSErrors = true,
        });

        await _noveldexContext.AddInitScriptAsync(@"
            Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
            window.chrome = { runtime: {}, loadTimes: function(){}, csi: function(){}, app: {} };
        ");

        // Warm up: navigate to noveldex home AND the series list so the browser
        // builds up real browsing history/cookies before any chapter is fetched.
        // noveldex checks referrer chain — home → series → chapter looks natural.
        Console.Write("  [noveldex] Warming up browser session...");
        var warmPage = await _noveldexContext.NewPageAsync();
        try
        {
            // Step 1: land on home
            await warmPage.GotoAsync("https://noveldex.io",
                new() { WaitUntil = WaitUntilState.Load, Timeout = 30000 });
            await Task.Delay(1500);

            // Step 2: navigate to the series list (simulates browsing)
            await warmPage.GotoAsync("https://noveldex.io/series?type=Light+Novel%2CWeb+Novel%2CPublished+Novel%2COriginal+Fiction%2COne+Shot%2CFanfiction%2CNovel",
                new() { WaitUntil = WaitUntilState.Load, Timeout = 30000 });
            await Task.Delay(2000);
        }
        catch { /* ignore warm-up failures */ }
        finally
        {
            // Keep the page open — noveldex may check that a tab is still alive
            _noveldexWarmPage = warmPage;
        }
        Console.WriteLine(" done");

        return _noveldexContext;
    }

    /// <summary>
    /// Waits for noveldex.io page hydration, then extracts content directly from
    /// the live DOM via JS — bypassing ContentAsync which always includes noscript
    /// blocks verbatim.
    ///
    /// For chapter pages: waits for paragraph content to appear, then returns a
    /// synthetic HTML string with the extracted text wrapped in shuka-extracted div.
    /// For series/index pages: waits for chapter links, then returns ContentAsync
    /// with noscript blocks stripped.
    /// Returns empty string if extraction fails (caller falls back to ContentAsync).
    /// </summary>
    private static async Task<string> WaitForNoveldexHydrationAsync(
        IPage page, string url, CancellationToken ct)
    {
        bool isChapterPage = url.Contains("/chapter/", StringComparison.OrdinalIgnoreCase);

        if (isChapterPage)
        {
            // ── Paywall / locked chapter fast-path ────────────────────────────
            // Check for coin-lock UI before waiting 40 seconds for hydration.
            // If the page body says "Unlock to continue reading" or "Sign in to Unlock"
            // we return a minimal HTML with those markers so ExtractChapterText
            // can detect it as paywalled and return empty (skip the chapter).
            try
            {
                string bodyText = await page.EvaluateAsync<string>(
                    "() => document.body ? document.body.innerText : ''");
                bool locked =
                    bodyText.Contains("Unlock to continue reading", StringComparison.OrdinalIgnoreCase) ||
                    bodyText.Contains("Sign in to Unlock",          StringComparison.OrdinalIgnoreCase) ||
                    (bodyText.Contains("coins",            StringComparison.OrdinalIgnoreCase) &&
                     bodyText.Contains("permanent access", StringComparison.OrdinalIgnoreCase));

                if (locked)
                {
                    Console.Write("[locked]");
                    return "<html><body><p>Unlock to continue reading</p><p>coinsSign in to Unlock</p></body></html>";
                }
            }
            catch { /* ignore — fall through to normal hydration wait */ }

            // Wait until real paragraph content is in the DOM.
            // Chapter paragraphs appear inside the main content area, not in nav/footer.
            // We check for at least 8 paragraphs with 80+ chars — footer only has ~2 short paragraphs.
            try
            {
                await page.WaitForFunctionAsync(
                    @"() => {
                        let count = 0;
                        const ps = document.querySelectorAll('p');
                        for (const p of ps) {
                            if ((p.innerText || '').trim().length > 80) count++;
                        }
                        return count >= 8;
                    }",
                    null,
                    new() { Timeout = 40000, PollingInterval = 600 });
            }
            catch (TimeoutException)
            {
                // One last paywall check after the timeout — the page may have
                // finished loading a locked state during the wait period.
                try
                {
                    string bodyText = await page.EvaluateAsync<string>(
                        "() => document.body ? document.body.innerText : ''");
                    bool locked =
                        bodyText.Contains("Unlock to continue reading", StringComparison.OrdinalIgnoreCase) ||
                        bodyText.Contains("Sign in to Unlock",          StringComparison.OrdinalIgnoreCase);
                    if (locked)
                    {
                        Console.Write("[locked]");
                        return "<html><body><p>Unlock to continue reading</p><p>coinsSign in to Unlock</p></body></html>";
                    }
                }
                catch { }

                Console.Write("[hydration-timeout]");
                return string.Empty;
            }

            // Extract paragraphs directly — completely bypasses ContentAsync/noscript
            try
            {
                var paragraphs = await page.EvaluateAsync<string[]>(@"
                    () => {
                        const result = [];
                        document.querySelectorAll('p').forEach(p => {
                            const t = (p.innerText || '').trim();
                            if (t.length > 10) result.push(t);
                        });
                        return result;
                    }");

                if (paragraphs != null && paragraphs.Length > 0)
                {
                    // Return as synthetic HTML that ExtractChapterText can parse
                    var sb = new System.Text.StringBuilder();
                    sb.Append("<html><body><div id=\"shuka-extracted\">");
                    foreach (string para in paragraphs)
                    {
                        string escaped = para
                            .Replace("&", "&amp;")
                            .Replace("<", "&lt;")
                            .Replace(">", "&gt;");
                        sb.Append("<p>").Append(escaped).Append("</p>");
                    }
                    sb.Append("</div></body></html>");
                    return sb.ToString();
                }
            }
            catch { /* fall through */ }

            return string.Empty;
        }
        else
        {
            // Series/index page: wait for chapter links and cover images then return stripped HTML
            try
            {
                await page.WaitForSelectorAsync(
                    "a[href*=\"/chapter/\"]",
                    new() { Timeout = 25000 });
            }
            catch (TimeoutException)
            {
                Console.Write("[index-timeout]");
            }

            // Wait for cover images to load (they're loaded dynamically)
            try
            {
                await page.WaitForFunctionAsync(
                    @"() => {
                        const imgs = document.querySelectorAll('img[src*=""media.noveldex.io""], img[src*=""cover""]');
                        return imgs.length > 0 && imgs[0].complete && imgs[0].naturalWidth > 0;
                    }",
                    null,
                    new() { Timeout = 10000 });
            }
            catch (TimeoutException)
            {
                // Cover image might not load, continue anyway
            }

            // Strip noscript blocks from the serialised DOM before returning
            string raw = await page.ContentAsync();
            return Regex.Replace(raw, @"<noscript[\s\S]*?</noscript>", "",
                RegexOptions.IgnoreCase);
        }
    }

    // ── Browser lifecycle ─────────────────────────────────────────────────────

    private async Task EnsureBrowserAsync()
    {
        if (_playwright != null) return;

        // Auto-install / update Chromium when the Playwright version changes
        string version = typeof(Microsoft.Playwright.Playwright)
            .Assembly.GetName().Version?.ToString() ?? "unknown";
        string markerPath = Path.Combine(AppContext.BaseDirectory, ".playwright-version");
        string installed  = File.Exists(markerPath) ? File.ReadAllText(markerPath).Trim() : "";

        if (installed != version)
        {
            Console.WriteLine(installed == ""
                ? "\n  [cloudflare] Installing browser (first run)..."
                : $"\n  [cloudflare] Updating browser ({installed} → {version})...");
            int exit = Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
            if (exit != 0)
                throw new Exception($"Playwright install failed (exit {exit}). Run: Shuka.exe playwright install chromium");
            File.WriteAllText(markerPath, version);
        }

        Console.WriteLine("\n  [cloudflare] Starting headless browser...");
        _playwright = await Playwright.CreateAsync();

        string userDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Shuka", "browser-profile");
        Directory.CreateDirectory(userDataDir);

        _context = await _playwright.Chromium.LaunchPersistentContextAsync(userDataDir, new()
        {
            Headless = true,
            Args     =
            [
                "--disable-blink-features=AutomationControlled",
                "--no-sandbox",
                "--disable-dev-shm-usage",
                "--disable-web-security",
                "--disable-features=IsolateOrigins,site-per-process",
                "--allow-running-insecure-content",
                "--disable-setuid-sandbox",
                "--ignore-certificate-errors",
            ],
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
            ExtraHTTPHeaders = new Dictionary<string, string>
            {
                ["Accept-Language"] = "en-US,en;q=0.9",
                ["Accept"]          = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"
            },
            ViewportSize = new() { Width = 1280, Height = 800 },
            JavaScriptEnabled = true,
        });

        // Stealth: patch all the common headless fingerprints
        await _context.AddInitScriptAsync(@"
            // Hide webdriver flag
            Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
            // Fake plugins
            Object.defineProperty(navigator, 'plugins', {
                get: () => {
                    const arr = [
                        { name: 'Chrome PDF Plugin', filename: 'internal-pdf-viewer', description: 'Portable Document Format', length: 1 },
                        { name: 'Chrome PDF Viewer',  filename: 'mhjfbmdgcfjbbpaeojofohoefgiehjai', description: '', length: 0 },
                        { name: 'Native Client',      filename: 'internal-nacl-plugin', description: '', length: 2 },
                    ];
                    arr.__proto__ = PluginArray.prototype;
                    return arr;
                }
            });
            // Fake languages matching Accept-Language header
            Object.defineProperty(navigator, 'languages', { get: () => ['en-US', 'en'] });
            // Chrome runtime
            window.chrome = { runtime: {}, loadTimes: function(){}, csi: function(){}, app: {} };
            // Permissions API spoof
            const originalQuery = window.navigator.permissions ? window.navigator.permissions.query : null;
            if (originalQuery) {
                window.navigator.permissions.query = (params) =>
                    params.name === 'notifications'
                        ? Promise.resolve({ state: Notification.permission })
                        : originalQuery(params);
            }
            // Remove headless-specific properties
            delete window.__playwright;
            delete window.__pw_manual;
            delete window.__selenium_unwrapped;
        ");
    }

    /// <summary>
    /// Opens a visible browser window so the user can solve a CF challenge manually.
    /// Cookies are saved to the persistent profile and reused by headless runs.
    /// </summary>
    public static async Task SolveCfInteractiveAsync(string targetUrl) =>
        await SolveInteractiveAsync(targetUrl, "CF cookies saved. Future downloads should work without retries.");

    /// <summary>
    /// Opens a visible browser window for noveldex.io so the user can trigger
    /// the chapter content to load (and optionally log in), saving the session
    /// to the persistent profile for reuse by headless runs.
    /// </summary>
    public static async Task SolveNoveldexInteractiveAsync(string targetUrl) =>
        await SolveInteractiveAsync(targetUrl, "Session saved. Run your download again — chapter content should now load.");

    private static async Task SolveInteractiveAsync(string targetUrl, string doneMessage)
    {
        string userDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Shuka", "browser-profile");
        Directory.CreateDirectory(userDataDir);

        Console.WriteLine($"Opening browser for: {targetUrl}");
        Console.WriteLine("Wait for the page to fully load, then press Enter here.");
        Console.WriteLine();

        using var pw  = await Playwright.CreateAsync();
        var ctx = await pw.Chromium.LaunchPersistentContextAsync(userDataDir, new()
        {
            Headless  = false,
            Args      = ["--disable-blink-features=AutomationControlled"],
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
            ViewportSize = new() { Width = 1280, Height = 800 },
        });

        var page = await ctx.NewPageAsync();
        await page.GotoAsync(targetUrl, new() { WaitUntil = WaitUntilState.Load, Timeout = 60000 });

        Console.Write("Waiting for page to load");
        var cts = new CancellationTokenSource();

        var autoWait = Task.Run(async () =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline && !cts.Token.IsCancellationRequested)
            {
                try
                {
                    string t = await page.TitleAsync();
                    bool isChallenge = t.Contains("Just a moment") || t.Contains("请稍候") ||
                                       t.Contains("Checking") || t.Contains("Security") ||
                                       string.IsNullOrWhiteSpace(t);
                    if (!isChallenge) { Console.WriteLine($"\nPage loaded: {t}"); return; }
                }
                catch { /* navigating */ }
                Console.Write(".");
                await Task.Delay(1000);
            }
        }, cts.Token);

        await Task.WhenAny(autoWait, Task.Run(() => Console.ReadLine()));
        cts.Cancel();

        await Task.Delay(2000);
        await ctx.CloseAsync();

        Console.WriteLine(doneMessage);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string DetectCharset(HttpResponseMessage resp, string latin1)
    {
        string charset = "utf-8";
        string? ct = resp.Content.Headers.ContentType?.CharSet;
        if (!string.IsNullOrWhiteSpace(ct))
        {
            charset = ct.Trim().Trim('"');
        }
        else
        {
            string head = latin1[..Math.Min(latin1.Length, 4096)];
            var m = Regex.Match(head, @"charset\s*=\s*[""']?\s*([\w-]+)", RegexOptions.IgnoreCase);
            if (m.Success) charset = m.Groups[1].Value.Trim();
        }

        return charset.ToLowerInvariant() switch
        {
            "gb2312" or "gb_2312" or "csgb2312" or "x-gbk" or "chinese" => "gbk",
            "big5"   or "csbig5"  or "x-x-big5"                         => "big5",
            _                                                             => charset
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_noveldexWarmPage is not null) try { await _noveldexWarmPage.CloseAsync(); } catch { }
        if (_noveldexContext is not null) await _noveldexContext.CloseAsync();
        if (_context is not null) await _context.CloseAsync();
        _playwright?.Dispose();
    }
}
