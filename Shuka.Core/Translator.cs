using System.Text;
using System.Text.Json;

namespace Shuka.Core;

public class Translator
{
    private readonly HttpClient _http;

    // Global cap on concurrent translate API calls across all downloads.
    // Google's unofficial API tolerates ~6 concurrent requests before throttling.
    private static readonly SemaphoreSlim _globalSem = new(6);

    // Max chars per chunk — Google Translate handles up to ~5000 chars reliably.
    // Larger chunks = fewer round-trips = faster overall.
    private const int ChunkSize = 4500;

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
            try   { return (i, text: await TranslateChunk(chunk, log, ct)); }
            finally { _globalSem.Release(); }
        }).ToArray();

        var results = await Task.WhenAll(tasks);
        return string.Join("\n", results.OrderBy(r => r.i).Select(r => r.text));
    }

    /// <summary>
    /// Translate one chunk. Tries Google first with a short retry window,
    /// then falls back to MyMemory, then gives up and returns the original.
    /// </summary>
    private async Task<string> TranslateChunk(string chunk, Action<string>? log,
        CancellationToken ct)
    {
        // ── Phase 1: Google Translate — up to 8 fast retries ─────────────────
        // Keep retries low so a rate-limit doesn't stall the whole pipeline.
        // If Google is throttling we fall through to MyMemory quickly.
        for (int attempt = 1; attempt <= 8; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string? result = await CallGoogle(chunk, ct);
                if (result != null) return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                int delayMs = ex is TaskCanceledException ? 1500
                            : Math.Min(500 * attempt, 5000);
                if (attempt < 8)
                {
                    log?.Invoke($"[Google retry {attempt}/8]");
                    await Task.Delay(delayMs, ct);
                }
            }
        }

        // ── Phase 2: MyMemory fallback ────────────────────────────────────────
        // MyMemory has a 500-char limit per request, so split large chunks.
        log?.Invoke("[Google failed, trying MyMemory]");
        try
        {
            string? mm = await CallMyMemory(chunk, ct);
            if (mm != null)
            {
                log?.Invoke("[MM ok]");
                return mm;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* fall through */ }

        // ── Phase 3: retry Google a few more times before giving up ──────────
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string? result = await CallGoogle(chunk, ct);
                if (result != null) return result;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                await Task.Delay(Math.Min(2000 * attempt, 10000), ct);
            }
        }

        log?.Invoke("[translation failed, keeping original]");
        return chunk;
    }

    private async Task<string?> CallGoogle(string chunk, CancellationToken ct)
    {
        string url = "https://translate.googleapis.com/translate_a/single" +
            $"?client=gtx&sl=zh&tl=en&dt=t&q={Uri.EscapeDataString(chunk)}";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(12)); // fail fast per request

        string json = await _http.GetStringAsync(url, cts.Token);
        using var jdoc = JsonDocument.Parse(json);

        var sb = new StringBuilder(chunk.Length * 2);
        foreach (var seg in jdoc.RootElement[0].EnumerateArray())
            if (seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0
                && seg[0].ValueKind == JsonValueKind.String)
                sb.Append(seg[0].GetString());

        string result = sb.ToString().Trim();
        return string.IsNullOrEmpty(result) ? null : result;
    }

    private async Task<string?> CallMyMemory(string chunk, CancellationToken ct)
    {
        // MyMemory caps at 500 chars — split and reassemble if needed
        if (chunk.Length <= 500)
        {
            return await CallMyMemoryChunk(chunk, ct);
        }

        var parts   = new List<string>();
        var results = new List<string>();

        for (int i = 0; i < chunk.Length; i += 500)
            parts.Add(chunk.Substring(i, Math.Min(500, chunk.Length - i)));

        foreach (var part in parts)
        {
            string? r = await CallMyMemoryChunk(part, ct);
            if (r == null) return null;
            results.Add(r);
        }

        return string.Join("", results);
    }

    private async Task<string?> CallMyMemoryChunk(string chunk, CancellationToken ct)
    {
        string url = $"https://api.mymemory.translated.net/get" +
            $"?q={Uri.EscapeDataString(chunk)}&langpair=zh|en";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        string json = await _http.GetStringAsync(url, cts.Token);
        using var jdoc = JsonDocument.Parse(json);

        string? result = jdoc.RootElement
            .GetProperty("responseData")
            .GetProperty("translatedText")
            .GetString();

        return !string.IsNullOrWhiteSpace(result) && result != chunk
            ? result.Trim()
            : null;
    }
}
