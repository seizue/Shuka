using System.Text;
using System.Text.Json;

namespace Shuka.Core;

public class Translator
{
    private readonly HttpClient _http;

    // Shared across ALL Translator instances — caps total concurrent translate
    // calls globally so multiple simultaneous downloads don't flood the API.
    private static readonly SemaphoreSlim _globalSem = new(3);

    public Translator(HttpClient http)
    {
        _http = http;
    }

    // Split text into chunks and translate in parallel
    public async Task<string> Translate(string text, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var lines  = text.Split('\n');
        var chunks = new List<string>();
        var cur    = new StringBuilder();

        foreach (var line in lines)
        {
            if (cur.Length + line.Length + 1 > 1500 && cur.Length > 0)
            { chunks.Add(cur.ToString()); cur.Clear(); }
            if (cur.Length > 0) cur.Append('\n');
            cur.Append(line);
        }
        if (cur.Length > 0) chunks.Add(cur.ToString());

        // Translate all chunks in parallel, gated by the global semaphore
        var tasks = chunks.Select(async (chunk, i) =>
        {
            await _globalSem.WaitAsync();
            try   { return (i, text: await TranslateChunk(chunk, log)); }
            finally { _globalSem.Release(); }
        }).ToList();

        var results = await Task.WhenAll(tasks);
        return string.Join("\n", results.OrderBy(r => r.i).Select(r => r.text));
    }

    // Translate a single chunk — 50 Google retries first, then alternates Google/MyMemory
    private async Task<string> TranslateChunk(string chunk, Action<string>? log = null)
    {
        // Phase 1: retry Google up to 50 times
        for (int attempt = 1; attempt <= 50; attempt++)
        {
            try
            {
                string url = "https://translate.googleapis.com/translate_a/single" +
                    $"?client=gtx&sl=zh&tl=en&dt=t&q={Uri.EscapeDataString(chunk)}";
                string json = await _http.GetStringAsync(url);
                using var jdoc = JsonDocument.Parse(json);
                var sb = new StringBuilder();
                foreach (var seg in jdoc.RootElement[0].EnumerateArray())
                    if (seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0
                        && seg[0].ValueKind == JsonValueKind.String)
                        sb.Append(seg[0].GetString());
                string r = sb.ToString().Trim();
                if (!string.IsNullOrEmpty(r)) return r;
            }
            catch (Exception ex)
            {
                int delay = ex is TaskCanceledException ? 2000 : Math.Min(1000 * attempt, 10000);
                log?.Invoke($"[Google retry {attempt}/50]");
                await Task.Delay(delay);
            }
        }

        log?.Invoke("[Google failed 50 times, switching to alternating fallback]");

        // Phase 2: alternate Google/MyMemory, max 100 each
        const int maxAttempts = 100;
        int googleFails = 0, memoryFails = 0;
        bool useGoogle = true;

        while (googleFails < maxAttempts || memoryFails < maxAttempts)
        {
            if (useGoogle && googleFails >= maxAttempts) useGoogle = false;
            if (!useGoogle && memoryFails >= maxAttempts) useGoogle = true;

            if (useGoogle)
            {
                try
                {
                    string url = "https://translate.googleapis.com/translate_a/single" +
                        $"?client=gtx&sl=zh&tl=en&dt=t&q={Uri.EscapeDataString(chunk)}";
                    string json = await _http.GetStringAsync(url);
                    using var jdoc = JsonDocument.Parse(json);
                    var sb = new StringBuilder();
                    foreach (var seg in jdoc.RootElement[0].EnumerateArray())
                        if (seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0
                            && seg[0].ValueKind == JsonValueKind.String)
                            sb.Append(seg[0].GetString());
                    string r = sb.ToString().Trim();
                    if (!string.IsNullOrEmpty(r)) return r;
                    googleFails++;
                }
                catch
                {
                    googleFails++;
                    log?.Invoke($"[Google fail #{googleFails}, switching to MyMemory]");
                    await Task.Delay(Math.Min(1000 * googleFails, 10000));
                }
                useGoogle = false;
            }
            else
            {
                try
                {
                    string q = chunk.Length > 500 ? chunk[..500] : chunk;
                    string url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(q)}&langpair=zh|en";
                    string json = await _http.GetStringAsync(url);
                    using var jdoc = JsonDocument.Parse(json);
                    string? result = jdoc.RootElement
                        .GetProperty("responseData")
                        .GetProperty("translatedText")
                        .GetString();
                    if (!string.IsNullOrWhiteSpace(result) && result != chunk)
                    {
                        log?.Invoke("[MM]");
                        return result.Trim();
                    }
                    memoryFails++;
                }
                catch
                {
                    memoryFails++;
                    log?.Invoke($"[MyMemory fail #{memoryFails}, switching to Google]");
                    await Task.Delay(Math.Min(1000 * memoryFails, 10000));
                }
                useGoogle = true;
            }
        }

        log?.Invoke("[all translators exhausted, keeping original]");
        return chunk;
    }
}
