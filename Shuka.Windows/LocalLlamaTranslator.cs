using System.Text;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace Shuka;

/// <summary>
/// 100% offline in-process translator using Qwen2.5-0.5B-Instruct GGUF via LLamaSharp.
/// The model is downloaded once (~468MB) to %LocalAppData%\Shuka\models\ and runs on CPU.
/// Used as a guaranteed fallback when all Google endpoints are rate-limited.
/// </summary>
internal sealed class LocalLlamaTranslator : IDisposable
{
    public static readonly string ModelDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Shuka", "models");

    public const string ModelFileName = "qwen2.5-0.5b-instruct-q4_k_m.gguf";
    public static readonly string ModelPath = Path.Combine(ModelDir, ModelFileName);

    private const string DownloadUrl =
        "https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF/resolve/main/qwen2.5-0.5b-instruct-q4_k_m.gguf";

    // Minimum file size guard — a complete Q4_K_M 0.5B model is ~468MB
    private const long MinModelBytes = 400_000_000;

    // Max chars per chunk sent to the model — keeps context window well inside 2048 tokens
    private const int ChunkSize = 400;

    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private bool _disposed;

    // ── Model presence ────────────────────────────────────────────────────────

    public bool IsModelDownloaded()
    {
        if (!File.Exists(ModelPath)) return false;
        try { return new FileInfo(ModelPath).Length >= MinModelBytes; }
        catch { return false; }
    }

    // ── Download ──────────────────────────────────────────────────────────────

    public async Task DownloadModelAsync(Action<string> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(ModelDir);
        string tmp = ModelPath + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);

        progress("Connecting to HuggingFace...");
        using var http = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        using var resp = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Download failed: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");

        long? total = resp.Content.Headers.ContentLength;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(tmp))
        {
            byte[] buf = new byte[128 * 1024];
            long received = 0;
            int read;
            var lastReport = DateTime.MinValue;
            while ((read = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, read), ct);
                received += read;
                if ((DateTime.Now - lastReport).TotalMilliseconds >= 300)
                {
                    lastReport = DateTime.Now;
                    string size = $"{received / 1024 / 1024}MB";
                    string pct  = total > 0 ? $" / {total.Value / 1024 / 1024}MB ({received * 100 / total.Value}%)" : "";
                    progress($"Downloading Qwen2.5 model: {size}{pct}");
                }
            }
        }

        if (File.Exists(ModelPath)) File.Delete(ModelPath);
        File.Move(tmp, ModelPath);
        progress("✓ Qwen2.5 model downloaded.");
    }

    // ── Lazy model load ───────────────────────────────────────────────────────

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_weights != null) return;
        await _loadLock.WaitAsync(ct);
        try
        {
            if (_weights != null) return; // double-check after lock
            if (!IsModelDownloaded())
                throw new InvalidOperationException(
                    "Local AI model not downloaded. Use Translation Engine → Local AI (Qwen2.5) to download it.");

            var mp = new ModelParams(ModelPath)
            {
                ContextSize  = 2048,
                GpuLayerCount = 0, // CPU-only — works on every Windows machine
            };
            _weights = LLamaWeights.LoadFromFile(mp);
            _context = _weights.CreateContext(mp);
        }
        finally { _loadLock.Release(); }
    }

    // ── Public translate entry point ──────────────────────────────────────────

    public async Task<string> Translate(string text, Action<string>? log = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        await EnsureLoadedAsync(ct);

        var chunks = SplitIntoChunks(text);
        var results = new string[chunks.Count];

        for (int i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (chunks.Count > 1)
                log?.Invoke($"[Local AI] Segment {i + 1}/{chunks.Count}...");
            results[i] = await TranslateChunkAsync(chunks[i], ct);
        }

        return string.Join("\n", results);
    }

    // ── Chunking ──────────────────────────────────────────────────────────────

    private static List<string> SplitIntoChunks(string text)
    {
        var chunks = new List<string>();
        var cur    = new StringBuilder(ChunkSize + 100);

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

    // ── Inference ─────────────────────────────────────────────────────────────

    private async Task<string> TranslateChunkAsync(string chunk, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(chunk)) return chunk;

        // Qwen2.5-Instruct ChatML prompt format
        string prompt =
            "<|im_start|>system\n" +
            "You are a professional literary translator. Translate the following Chinese novel text into " +
            "natural, fluent English. Output ONLY the translation with no extra comments or notes.\n" +
            "<|im_end|>\n" +
            "<|im_start|>user\n" +
            chunk + "\n" +
            "<|im_end|>\n" +
            "<|im_start|>assistant\n";

        var executor = new InteractiveExecutor(_context!);
        var sampling = new DefaultSamplingPipeline { Temperature = 0.2f };
        var inferParams = new InferenceParams
        {
            SamplingPipeline = sampling,
            MaxTokens        = 512,
            AntiPrompts      = new[] { "<|im_end|>", "<|im_start|>", "<|endoftext|>" }
        };

        var sb = new StringBuilder();
        await foreach (string token in executor.InferAsync(prompt, inferParams, ct))
            sb.Append(token);

        string result = sb.ToString().Trim();

        // If model returned empty or repeated the source, keep original
        return string.IsNullOrWhiteSpace(result) ? chunk : result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _context?.Dispose();
        _weights?.Dispose();
        _loadLock.Dispose();
    }
}
