using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;

namespace Shuka.Core;

public class Translator
{
    private readonly HttpClient _http;

    // 3 concurrent translate calls — balanced between speed and avoiding rate limits.
    private static readonly SemaphoreSlim _globalSem = new(3);

    // 800 chars — comfortable chunk size
    private const int ChunkSize = 800;

    public Translator(HttpClient http)
    {
        _http = http;
    }

    /// <summary>Split text into chunks and translate them in parallel.</summary>
    public async Task<string> Translate(string text, Action<string>? log = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

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

        // ── Translate all chunks in parallel ──────────────────────────────────
        var tasks = chunks.Select(async (chunk, i) =>
        {
            await _globalSem.WaitAsync(ct);
            try
            {
                return (i, text: await TranslateChunk(chunk, log, ct));
            }
            finally
            {
                // Brief cooldown after each chunk before releasing the slot —
                // spreads requests over time without blocking the semaphore queue.
                await Task.Delay(200, ct).ConfigureAwait(false);
                _globalSem.Release();
            }
        }).ToArray();

        var results = await Task.WhenAll(tasks);
        return string.Join("\n", results.OrderBy(r => r.i).Select(r => r.text));
    }

    /// <summary>
    /// Translate one chunk. Tries Google GTX API (GET/POST) first, then MyMemory, then legacy web.
    /// Logs every failure so the user can see what's happening.
    /// </summary>
    private async Task<string> TranslateChunk(string chunk, Action<string>? log,
        CancellationToken ct)
    {
        // ── Endpoint 1: Google GTX GET / POST API ────────────────────────────
        // Direct, fast, works across both Simplified and Traditional Chinese.
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // If chunk is large (> 400 chars), prefer POST to avoid query string length limits
                string? result = chunk.Length > 400
                    ? await CallGooglePost(chunk, ct)
                    : await CallGoogleGtx(chunk, ct);

                if (result != null) return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                log?.Invoke($"[Google GTX timeout attempt {attempt}/3]");
                if (attempt < 3) await Task.Delay(300 * attempt, ct);
            }
            catch (Exception ex)
            {
                log?.Invoke($"[Google GTX attempt {attempt}/3 failed: {ex.Message}]");
                if (attempt < 3) await Task.Delay(300 * attempt, ct);
            }
        }

        // ── Endpoint 2: Google GTX POST (if GET was tried first or vice versa) ─
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string? result = chunk.Length > 400
                    ? await CallGoogleGtx(chunk, ct)
                    : await CallGooglePost(chunk, ct);

                if (result != null) return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                if (attempt < 2) await Task.Delay(400 * attempt, ct);
            }
            catch (Exception)
            {
                if (attempt < 2) await Task.Delay(400 * attempt, ct);
            }
        }

        // ── Endpoint 3: MyMemory (reliable free fallback) ─────────────────────
        try
        {
            string? mm = await CallMyMemory(chunk, ct);
            if (mm != null)
            {
                log?.Invoke("[MyMemory fallback ok]");
                return mm;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log?.Invoke($"[MyMemory failed: {ex.Message}]");
        }

        // ── Endpoint 4: Google mobile web page (last resort scraper) ──────────
        try
        {
            string truncated = chunk.Length > 600 ? chunk[..600] : chunk;
            string? result = await CallGoogleMobileWeb(truncated, ct);
            if (result != null) return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* ignored as mobile endpoint is usually 429 */ }

        // ── Last resort: return original text ─────────────────────────────────
        log?.Invoke("[all translation endpoints failed — keeping original]");
        return chunk;
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

    /// <summary>
    /// Google Translate via POST to the web API — avoids URL length limits.
    /// </summary>
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
            throw new Exception("Got HTML instead of JSON");

        return ParseGoogleJson(json, chunk);
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
        var m = System.Text.RegularExpressions.Regex.Match(html,
            @"class=""result-container""[^>]*>([\s\S]*?)</div>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return null;

        string result = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
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
