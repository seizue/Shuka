using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Shuka.Core;

public class Translator
{
    private readonly HttpClient _http;

    // ── Adaptive Concurrency ──────────────────────────────────────────────────
    // Standard mode: 3 concurrent requests with 200ms delay.
    // Throttled mode (rate-limited / error recovery): 1 request at a time with 1500ms delay.
    private static readonly SemaphoreSlim _standardSem = new(3, 3);
    private static readonly SemaphoreSlim _throttledSem = new(1, 1);

    // 800 chars — comfortable chunk size
    private const int ChunkSize = 800;

    // ── Cross-chapter rate-limit & Circuit Breaker tracking ───────────────────
    private static bool _googleRateLimited;
    private static DateTime _googleRateLimitReset = DateTime.MinValue;
    private static readonly object _rateLimitLock = new();
    private static bool _isProbingGoogle = false;

    // ── Bing session caching ──────────────────────────────────────────────────
    private static string? _bingIg;
    private static string? _bingIid;
    private static string? _bingKey;
    private static string? _bingToken;
    private static DateTime _bingTokenExpiry = DateTime.MinValue;
    private static readonly SemaphoreSlim _bingAuthLock = new(1, 1);

    private static readonly Regex SignificantChineseRegex =
        new(@"[\u4e00-\u9fff\u3400-\u4dbf\uf900-\ufaff]{8,}", RegexOptions.Compiled);

    private static readonly Regex AnyChineseRegex =
        new(@"[\u4e00-\u9fff\u3400-\u4dbf\uf900-\ufaff]", RegexOptions.Compiled);

    public static bool IsGoogleRateLimited
    {
        get
        {
            lock (_rateLimitLock)
            {
                return _googleRateLimited && DateTime.UtcNow < _googleRateLimitReset;
            }
        }
    }

    public static void MarkGoogleRateLimited(TimeSpan? duration = null)
    {
        lock (_rateLimitLock)
        {
            _googleRateLimited = true;
            _googleRateLimitReset = DateTime.UtcNow + (duration ?? TimeSpan.FromMinutes(20));
        }
    }

    public static void ClearGoogleRateLimit()
    {
        lock (_rateLimitLock)
        {
            _googleRateLimited = false;
            _googleRateLimitReset = DateTime.MinValue;
        }
    }

    /// <summary>
    /// Restores semaphore permits if a previous task was cancelled before release.
    /// </summary>
    public static void ResetSemaphores()
    {
        try { while (_standardSem.CurrentCount < 3) _standardSem.Release(); } catch { }
        try { while (_throttledSem.CurrentCount < 1) _throttledSem.Release(); } catch { }
    }

    public static bool IsUntranslatedChinese(string original, string result)
    {
        if (string.IsNullOrWhiteSpace(result)) return true;
        if (!AnyChineseRegex.IsMatch(original)) return false;

        // If the translation resulted in the exact same text, it was echoed/untranslated
        if (string.Equals(result.Trim(), original.Trim(), StringComparison.Ordinal))
            return true;

        // If short text (e.g. title) still contains any Chinese characters
        if (original.Length < 30)
            return AnyChineseRegex.IsMatch(result);

        // Count Chinese characters in the result (including ideographs across standard CJK blocks)
        int chineseCount = 0;
        for (int i = 0; i < result.Length; i++)
        {
            char c = result[i];
            if ((c >= 0x4e00 && c <= 0x9fff) ||
                (c >= 0x3400 && c <= 0x4dbf) ||
                (c >= 0xf900 && c <= 0xfaff))
            {
                chineseCount++;
            }
        }

        // If there are 5 or more Chinese characters, it definitely contains untranslated Chinese
        if (chineseCount >= 5)
            return true;

        // Or if Chinese characters make up more than 5% of the string
        if (result.Length > 0 && (double)chineseCount / result.Length > 0.05)
            return true;

        return false;
    }

    /// <summary>
    /// Detects if an exception was caused by the OS terminating or aborting network sockets,
    /// which happens on Android when switching apps or entering battery power save (ECONNABORTED).
    /// </summary>
    public static bool IsConnectionAbortedByOs(Exception ex)
    {
        for (Exception? cur = ex; cur != null; cur = cur.InnerException)
        {
            if (cur is System.Net.Sockets.SocketException se &&
                (se.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionAborted ||
                 se.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionReset ||
                 se.NativeErrorCode == 103 || se.NativeErrorCode == 10053))
            {
                return true;
            }
            if (cur.Message.Contains("Software caused connection abort", StringComparison.OrdinalIgnoreCase) ||
                cur.Message.Contains("connection abort", StringComparison.OrdinalIgnoreCase) ||
                cur.Message.Contains("ECONNABORTED", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public Translator(HttpClient http)
    {
        _http = http;
    }

    /// <summary>Split text into chunks and translate them with adaptive concurrency.</summary>
    public async Task<string> Translate(string text, Action<string>? log = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        if (!AnyChineseRegex.IsMatch(text)) return text;

        // ── Build chunks ──────────────────────────────────────────────────────
        var chunks = new List<string>();
        var cur    = new StringBuilder(ChunkSize + 200);

        foreach (var line in text.Split('\n'))
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

        // ── Translate chunks with adaptive throttling ─────────────────────────
        var tasks = chunks.Select(async (chunk, i) =>
        {
            // Pick semaphore based on rate-limit status (1 at a time when throttled, 3 when normal)
            var sem = IsGoogleRateLimited ? _throttledSem : _standardSem;
            await sem.WaitAsync(ct);
            try
            {
                return (i, text: await TranslateChunk(chunk, log, ct));
            }
            finally
            {
                try
                {
                    if (!ct.IsCancellationRequested)
                    {
                        int delayMs = IsGoogleRateLimited ? 1500 : 200;
                        await Task.Delay(delayMs, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch { }
                finally
                {
                    sem.Release();
                }
            }
        }).ToArray();

        var results = await Task.WhenAll(tasks);
        return string.Join("\n", results.OrderBy(r => r.i).Select(r => r.text));
    }

    /// <summary>
    /// Translates a single chunk through multi-layer fallbacks:
    /// 1. Google GTX API (GET/POST) — fastest when not rate-limited
    /// 2. Bing Translator — reliable independent Microsoft backend
    /// 3. MyMemory — free community translation API
    /// 4. Google Mobile Web — scraping fallback
    /// If all fail, throws TranslationFailedException so callers can track untranslated chapters.
    /// </summary>
    private async Task<string> TranslateChunk(string chunk, Action<string>? log,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(chunk)) return chunk;
        if (!AnyChineseRegex.IsMatch(chunk)) return chunk;

        // ── 1. Google GTX Circuit Breaker & Single-Probe Recovery ────────────
        bool canTryGoogle = false;
        bool isProbe = false;

        lock (_rateLimitLock)
        {
            if (!_googleRateLimited)
            {
                canTryGoogle = true;
            }
            else if (DateTime.UtcNow >= _googleRateLimitReset)
            {
                // Cooldown period expired (20 mins).
                // Use a single-probe pattern so we test Google with only 1 request rather than a burst of parallel chunks.
                if (!_isProbingGoogle)
                {
                    _isProbingGoogle = true;
                    isProbe = true;
                    canTryGoogle = true;
                }
            }
        }

        if (canTryGoogle)
        {
            if (isProbe)
            {
                log?.Invoke("[Google 20-min cooldown expired — probing Google with 1 chunk to verify recovery...]");
            }

            for (int attempt = 1; attempt <= 2; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    string? result = chunk.Length > 400
                        ? await CallGooglePost(chunk, ct)
                        : await CallGoogleGtx(chunk, ct);

                    if (result != null)
                    {
                        if (!IsUntranslatedChinese(chunk, result))
                        {
                            if (isProbe)
                            {
                                log?.Invoke("[Google recovery probe succeeded! Google Translate is primary again]");
                                ClearGoogleRateLimit();
                            }
                            return result;
                        }

                        log?.Invoke("[Google GTX returned untranslated text — rate limit active, switching to Bing (20m cooldown)]");
                        MarkGoogleRateLimited(TimeSpan.FromMinutes(20));
                        break;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (OperationCanceledException)
                {
                    log?.Invoke($"[Google GTX timeout attempt {attempt}/2]");
                    if (attempt < 2) await Task.Delay(400 * attempt, ct);
                }
                catch (Exception ex)
                {
                    if (IsConnectionAbortedByOs(ex))
                        log?.Invoke($"[Google failed: Connection aborted by OS power saving when switching apps. Tip: set Shuka battery to 'Unrestricted']");
                    else
                        log?.Invoke($"[Google GTX attempt {attempt}/2 failed: {ex.Message}]");

                    if (ex.Message.Contains("429") || ex.Message.Contains("rate limit") || ex.Message.Contains("HTML"))
                    {
                        log?.Invoke("[Google rate limit detected (429/blocked) — switching to Bing (20m cooldown)]");
                        MarkGoogleRateLimited(TimeSpan.FromMinutes(20));
                        break;
                    }
                    if (attempt < 2) await Task.Delay(400 * attempt, ct);
                }
            }

            if (isProbe)
            {
                lock (_rateLimitLock)
                {
                    _isProbingGoogle = false;
                    // If probe failed or returned untranslated, re-extend cooldown for another 20 minutes
                    if (_googleRateLimited)
                    {
                        _googleRateLimitReset = DateTime.UtcNow + TimeSpan.FromMinutes(20);
                        log?.Invoke("[Google recovery probe did not succeed — remaining on Bing for another 20 minutes]");
                    }
                }
            }
        }
        else
        {
            TimeSpan remaining = TimeSpan.Zero;
            lock (_rateLimitLock) { remaining = _googleRateLimitReset - DateTime.UtcNow; }
            int remMins = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
            log?.Invoke($"[Google cooldown active ({remMins}m remaining) — translating via Bing]");
        }

        // ── 2. Bing Translator (independent high-quality fallback) ───────────
        try
        {
            string? bingRes = await CallBingTranslator(chunk, ct, "auto-detect");
            if (bingRes == null || IsUntranslatedChinese(chunk, bingRes))
            {
                // If auto-detect returned untranslated Chinese, retry once explicitly with zh-Hans
                if (AnyChineseRegex.IsMatch(chunk))
                {
                    bingRes = await CallBingTranslator(chunk, ct, "zh-Hans");
                }
            }

            if (bingRes != null && !IsUntranslatedChinese(chunk, bingRes))
            {
                log?.Invoke("[Bing Translator fallback ok]");
                return bingRes;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            if (IsConnectionAbortedByOs(ex))
                log?.Invoke($"[Bing failed: Connection aborted by OS power saving when switching apps. Tip: set Shuka battery to 'Unrestricted' in phone settings]");
            else
                log?.Invoke($"[Bing failed: {ex.Message}]");
        }

        // ── 3. MyMemory API ───────────────────────────────────────────────────
        try
        {
            string? mm = await CallMyMemory(chunk, ct);
            if (mm != null && !IsUntranslatedChinese(chunk, mm))
            {
                log?.Invoke("[MyMemory fallback ok]");
                return mm;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            if (IsConnectionAbortedByOs(ex))
                log?.Invoke($"[MyMemory failed: Connection aborted by OS power saving when switching apps. Tip: set Shuka battery to 'Unrestricted']");
            else
                log?.Invoke($"[MyMemory failed: {ex.Message}]");
        }

        // ── 4. Google Mobile Web scraper ─────────────────────────────────────
        try
        {
            string truncated = chunk.Length > 600 ? chunk[..600] : chunk;
            string? result = await CallGoogleMobileWeb(truncated, ct);
            if (result != null && !IsUntranslatedChinese(chunk, result))
                return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* ignored as mobile endpoint is usually 429 when GTX is */ }

        // ── All endpoints failed: throw so caller can record failure ──────────
        log?.Invoke("[all translation endpoints failed for chunk]");
        throw new TranslationFailedException("All translation endpoints failed or were rate-limited.");
    }

    /// <summary>Google Translate unofficial GET endpoint (gtx client).</summary>
    private async Task<string?> CallGoogleGtx(string chunk, CancellationToken ct)
    {
        string url = "https://translate.googleapis.com/translate_a/single" +
            $"?client=gtx&sl=auto&tl=en&dt=t&q={Uri.EscapeDataString(chunk)}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        var resp = await _http.SendAsync(req, cts.Token);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"HTTP {(int)resp.StatusCode}");

        string json = await resp.Content.ReadAsStringAsync(cts.Token);
        if (json.TrimStart().StartsWith('<'))
            throw new Exception("Got HTML instead of JSON (rate limited or blocked)");

        return ParseGoogleJson(json, chunk);
    }

    /// <summary>Google Translate via POST to avoid URL length limits.</summary>
    private async Task<string?> CallGooglePost(string chunk, CancellationToken ct)
    {
        const string url = "https://translate.googleapis.com/translate_a/single" +
            "?client=gtx&sl=auto&tl=en&dt=t";

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        req.Content = new StringContent($"q={Uri.EscapeDataString(chunk)}", Encoding.UTF8, "application/x-www-form-urlencoded");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        var resp = await _http.SendAsync(req, cts.Token);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"HTTP {(int)resp.StatusCode}");

        string json = await resp.Content.ReadAsStringAsync(cts.Token);
        if (json.TrimStart().StartsWith('<'))
            throw new Exception("Got HTML instead of JSON (rate limited or blocked)");

        return ParseGoogleJson(json, chunk);
    }

    /// <summary>
    /// Microsoft Bing Translator web API. Caches session parameters for up to 10 minutes.
    /// </summary>
    private async Task<string?> CallBingTranslator(string chunk, CancellationToken ct, string fromLang = "auto-detect")
    {
        await EnsureBingSessionAsync(ct);
        if (_bingToken == null || _bingKey == null)
            return null;

        string postUrl = $"https://www.bing.com/ttranslatev3?isVertical=1&IG={_bingIg}&IID={_bingIid}";
        var form = new Dictionary<string, string>
        {
            { "fromLang", fromLang },
            { "to", "en" },
            { "text", chunk },
            { "token", _bingToken },
            { "key", _bingKey }
        };

        using var postReq = new HttpRequestMessage(HttpMethod.Post, postUrl);
        postReq.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        postReq.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        postReq.Content = new FormUrlEncodedContent(form);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(12));

        var resp = await _http.SendAsync(postReq, cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            // Reset cached session on HTTP failure
            _bingToken = null;
            throw new Exception($"Bing HTTP {(int)resp.StatusCode}");
        }

        string json = await resp.Content.ReadAsStringAsync(cts.Token);
        using var jdoc = JsonDocument.Parse(json);
        var root = jdoc.RootElement;
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
        {
            var first = root[0];
            if (first.TryGetProperty("translations", out var transArray) && transArray.GetArrayLength() > 0)
            {
                string? text = transArray[0].GetProperty("text").GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    return text.Trim();
            }
        }

        return null;
    }

    private async Task EnsureBingSessionAsync(CancellationToken ct)
    {
        if (_bingToken != null && DateTime.UtcNow < _bingTokenExpiry)
            return;

        await _bingAuthLock.WaitAsync(ct);
        try
        {
            if (_bingToken != null && DateTime.UtcNow < _bingTokenExpiry)
                return;

            using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.bing.com/translator");
            req.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            var resp = await _http.SendAsync(req, cts.Token);
            string html = await resp.Content.ReadAsStringAsync(cts.Token);

            var igMatch = Regex.Match(html, @"IG:""([a-zA-Z0-9]+)""");
            _bingIg = igMatch.Success ? igMatch.Groups[1].Value : "";

            var iidMatch = Regex.Match(html, @"data-iid=""([^""]+)""");
            _bingIid = iidMatch.Success ? iidMatch.Groups[1].Value : "translator.5026";

            var helperMatch = Regex.Match(html, @"params_AbusePreventionHelper\s*=\s*\[\s*(\d+)\s*,\s*""([^""]+)""");
            if (helperMatch.Success)
            {
                _bingKey = helperMatch.Groups[1].Value;
                _bingToken = helperMatch.Groups[2].Value;
                _bingTokenExpiry = DateTime.UtcNow + TimeSpan.FromMinutes(10);
            }
        }
        finally
        {
            _bingAuthLock.Release();
        }
    }

    /// <summary>Google Translate mobile web page — scrapes result-container div.</summary>
    private async Task<string?> CallGoogleMobileWeb(string chunk, CancellationToken ct)
    {
        string url = $"https://translate.google.com/m?sl=auto&tl=en&q={Uri.EscapeDataString(chunk)}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Linux; Android 10; Mobile) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Mobile Safari/537.36");
        req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(8));

        var resp = await _http.SendAsync(req, cts.Token);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"HTTP {(int)resp.StatusCode}");

        string html = await resp.Content.ReadAsStringAsync(cts.Token);
        var m = Regex.Match(html,
            @"class=""result-container""[^>]*>([\s\S]*?)</div>",
            RegexOptions.IgnoreCase);
        if (!m.Success) return null;

        string result = WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
        return string.IsNullOrEmpty(result) ? null : result;
    }

    private static string? ParseGoogleJson(string json, string original)
    {
        using var jdoc = JsonDocument.Parse(json);
        var root = jdoc.RootElement;

        // Response format: [[["translated","original",null,null,10],...],...]
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            return null;

        var firstArray = root[0];
        if (firstArray.ValueKind != JsonValueKind.Array)
            return null;

        var sb = new StringBuilder(original.Length * 2);
        foreach (var seg in firstArray.EnumerateArray())
        {
            if (seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0
                && seg[0].ValueKind == JsonValueKind.String)
                sb.Append(seg[0].GetString());
        }

        string result = sb.ToString().Trim();
        return string.IsNullOrEmpty(result) ? null : result;
    }

    private async Task<string?> CallMyMemory(string chunk, CancellationToken ct)
    {
        // MyMemory caps at 500 chars — split and reassemble
        if (chunk.Length <= 450)
            return await CallMyMemoryChunk(chunk, ct);

        var parts   = new List<string>();
        var results = new List<string>();

        for (int i = 0; i < chunk.Length; i += 400)
            parts.Add(chunk.Substring(i, Math.Min(400, chunk.Length - i)));

        foreach (var part in parts)
        {
            string? r = await CallMyMemoryChunk(part, ct);
            if (r == null) return null;
            results.Add(r);
            if (parts.Count > 1)
                await Task.Delay(100, ct);
        }

        return string.Join("", results);
    }

    private async Task<string?> CallMyMemoryChunk(string chunk, CancellationToken ct)
    {
        string url = "https://api.mymemory.translated.net/get" +
            $"?q={Uri.EscapeDataString(chunk)}&langpair=zh|en-US";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        var resp = await _http.SendAsync(req, cts.Token);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"HTTP {(int)resp.StatusCode}");

        string json = await resp.Content.ReadAsStringAsync(cts.Token);
        using var jdoc = JsonDocument.Parse(json);

        int responseStatus = jdoc.RootElement
            .GetProperty("responseStatus")
            .GetInt32();

        if (responseStatus != 200)
            throw new Exception($"MyMemory status {responseStatus}");

        string? result = jdoc.RootElement
            .GetProperty("responseData")
            .GetProperty("translatedText")
            .GetString();

        return !string.IsNullOrWhiteSpace(result) && result != chunk
            ? result.Trim()
            : null;
    }
}
