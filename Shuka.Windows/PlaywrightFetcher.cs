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

    public PlaywrightFetcher(HttpClient siteClient)
    {
        _site = siteClient;
    }

    // ── Public fetch entry point ──────────────────────────────────────────────

    /// <summary>
    /// Fetches a URL. For 69shuba.com goes straight to Playwright;
    /// for other sites tries plain HTTP first and falls back on CF detection.
    /// </summary>
    public async Task<string> FetchAsync(string url, int retries = 4)
    {
        // Known CF-protected site — skip HTTP entirely
        if (url.Contains("69shuba.com", StringComparison.OrdinalIgnoreCase))
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

        var page = await _context!.NewPageAsync();
        try
        {
            ct.ThrowIfCancellationRequested();
            await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.Load, Timeout = 45000 });

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
            Args     = ["--disable-blink-features=AutomationControlled", "--no-sandbox", "--disable-dev-shm-usage"],
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
            ExtraHTTPHeaders = new Dictionary<string, string>
            {
                ["Accept-Language"] = "zh-CN,zh;q=0.9,en;q=0.7",
                ["Accept"]          = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"
            },
            ViewportSize = new() { Width = 1280, Height = 800 },
        });

        await _context.AddInitScriptAsync(@"
            Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
            Object.defineProperty(navigator, 'plugins',  { get: () => [1, 2, 3] });
            Object.defineProperty(navigator, 'languages',{ get: () => ['zh-CN','zh','en'] });
            window.chrome = { runtime: {} };
        ");
    }

    /// <summary>
    /// Opens a visible browser window so the user can solve a CF challenge manually.
    /// Cookies are saved to the persistent profile and reused by headless runs.
    /// </summary>
    public static async Task SolveCfInteractiveAsync(string targetUrl)
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

        Console.WriteLine("CF cookies saved. Future downloads should work without retries.");
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
        if (_context is not null) await _context.CloseAsync();
        _playwright?.Dispose();
    }
}
