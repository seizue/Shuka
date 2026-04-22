using System.Text.RegularExpressions;
using System.Threading.Channels;
using Shuka.Core.Adapters;

namespace Shuka.Core;

/// <summary>
/// Orchestrates gathering book info, downloading chapters, translating, and building the EPUB.
/// Platform-agnostic — used by both the Windows CLI and the Android app.
/// </summary>
public class BookService
{
    private readonly HttpFetcher _fetcher;
    private readonly HttpClient  _gtClient;
    private readonly Translator  _translator;

    private static readonly ISiteAdapter[] Adapters =
        [new ShukuAdapter(), new CzBooksAdapter()];

    public BookService(ICloudflareBypass? cfBypass = null)
    {
        _fetcher = new HttpFetcher(cfBypass);

        var gh = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
        _gtClient = new HttpClient(gh) { Timeout = TimeSpan.FromSeconds(45) };
        _gtClient.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");

        _translator = new Translator(_gtClient);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task<BookInfo> GatherBookInfo(string indexUrl, int chapterLimit = 0,
        string? forceCoverUrl = null, Action<string>? log = null,
        CancellationToken ct = default)
    {
        var adapter = DetectAdapter(indexUrl);
        indexUrl = adapter.NormalizeUrl(indexUrl);
        log?.Invoke($"Gathering [{adapter.SiteName}]: {indexUrl}");

        string html = await _fetcher.Fetch(indexUrl, log: log, ct: ct);
        var info = adapter.ParseIndex(html, indexUrl);
        int total = chapterLimit > 0 ? Math.Min(chapterLimit, info.ChapterUrls.Count) : info.ChapterUrls.Count;
        string? coverUrl = forceCoverUrl ?? info.CoverUrl ?? TryExtractCover(html, indexUrl);

        return new BookInfo(indexUrl, info.Title, info.Author, info.ChapterUrls, total, chapterLimit, coverUrl, adapter);
    }

    public async Task<string> ProcessBook(BookInfo book, string outputPath,
        IProgress<ProgressEventArgs>? progress = null, Action<string>? log = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        log?.Invoke($"Translating title/author...");
        book.TitleEn  = await _translator.Translate(book.Title,  log);
        book.AuthorEn = await _translator.Translate(book.Author, log);
        log?.Invoke($"Title (EN): {book.TitleEn}  Author (EN): {book.AuthorEn}");

        ct.ThrowIfCancellationRequested();
        var (coverBytes, coverMime) = await DownloadCover(book.CoverUrl, log);
        var chapters = await DownloadChapters(book, progress, log, ct);

        ct.ThrowIfCancellationRequested();
        log?.Invoke("Building EPUB...");
        if (File.Exists(outputPath)) File.Delete(outputPath);
        EpubBuilder.Build(outputPath, book.Title, book.TitleEn!, book.Author, book.AuthorEn!,
            chapters, coverBytes, coverMime);

        return outputPath;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// True parallel fetch + translate pipeline.
    /// Up to 4 chapters are fetched concurrently; up to 3 are translated concurrently.
    /// Translation starts as soon as a chapter's HTML arrives — no waiting for the
    /// whole fetch batch to finish first.
    /// Results are collected in original order.
    /// </summary>
    private async Task<List<(int Idx, string Title, string Text)>> DownloadChapters(
        BookInfo book, IProgress<ProgressEventArgs>? progress, Action<string>? log,
        CancellationToken ct = default)
    {
        var chapterList = book.ChapterUrls.Take(book.Total).ToList();
        int total = chapterList.Count;

        // Channel carries (index, title, raw html) from fetchers to translators
        var channel = Channel.CreateBounded<(int i, string title, string html)>(
            new BoundedChannelOptions(8)
            {
                SingleWriter = false,
                SingleReader = false,
                FullMode     = BoundedChannelFullMode.Wait
            });

        var fetchSem     = new SemaphoreSlim(4);   // max 4 concurrent fetches
        var translateSem = new SemaphoreSlim(3);   // max 3 concurrent translations

        // ── Stage 1: fetch all chapters, write to channel ─────────────────────
        var fetchProducer = Task.Run(async () =>
        {
            try
            {
                var fetchTasks = chapterList.Select(async (ch, i) =>
                {
                    await fetchSem.WaitAsync(ct);
                    try
                    {
                        string html = await _fetcher.Fetch(ch.Url, log: log, ct: ct);
                        await channel.Writer.WriteAsync((i, ch.Title, html), ct);
                    }
                    finally { fetchSem.Release(); }
                });

                await Task.WhenAll(fetchTasks);
            }
            finally
            {
                channel.Writer.Complete();
            }
        }, ct);

        // ── Stage 2: read from channel, translate in parallel, store results ──
        var results = new (string title, string text)?[total];
        int completed = 0;

        var translateTasks = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            await foreach (var (i, chTitle, html) in channel.Reader.ReadAllAsync(ct))
            {
                await translateSem.WaitAsync(ct);
                try
                {
                    var paras   = book.Adapter.ExtractChapterText(html);
                    string text = await _translator.Translate(string.Join("\n", paras), log);
                    results[i]  = (chTitle, text);

                    int done = Interlocked.Increment(ref completed);
                    progress?.Report(new ProgressEventArgs
                    {
                        Current = done,
                        Total   = total,
                        Message = $"Translated chapter {done} of {total}..."
                    });
                }
                finally { translateSem.Release(); }
            }
        }, ct)).ToArray();

        await Task.WhenAll(fetchProducer);
        await Task.WhenAll(translateTasks);

        // Assemble in original order
        return results
            .Select((r, i) => (i + 1, r!.Value.title, r!.Value.text))
            .ToList();
    }

    private async Task<(byte[]? bytes, string mime)> DownloadCover(string? coverUrl, Action<string>? log)
    {
        if (string.IsNullOrWhiteSpace(coverUrl)) return (null, "image/jpeg");
        log?.Invoke("Downloading cover...");
        try
        {
            byte[] bytes = await _gtClient.GetByteArrayAsync(coverUrl);
            string ext = Path.GetExtension(new Uri(coverUrl).AbsolutePath).ToLowerInvariant();
            string mime = ext switch { ".png" => "image/png", ".gif" => "image/gif", ".webp" => "image/webp", _ => "image/jpeg" };
            if (bytes.Length >= 4)
            {
                if (bytes[0] == 0x89 && bytes[1] == 0x50) mime = "image/png";
                else if (bytes[0] == 0xFF && bytes[1] == 0xD8) mime = "image/jpeg";
                else if (bytes[0] == 0x47 && bytes[1] == 0x49) mime = "image/gif";
            }
            log?.Invoke($"Cover OK ({bytes.Length / 1024}KB, {mime})");
            return (bytes, mime);
        }
        catch (Exception ex)
        {
            log?.Invoke($"Cover failed: {ex.Message} (using generated cover)");
            return (null, "image/jpeg");
        }
    }

    private static ISiteAdapter DetectAdapter(string url) =>
        Adapters.FirstOrDefault(a => a.Matches(url))
        ?? throw new Exception($"No supported adapter for URL: {url}\nSupported: 52shuku.net, czbooks.net");

    private static string? TryExtractCover(string html, string baseUrl)
    {
        var og = Regex.Match(html, @"<meta[^>]+property=[""']og:image[""'][^>]+content=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        if (!og.Success)
            og = Regex.Match(html, @"<meta[^>]+content=[""']([^""']+)[""'][^>]+property=[""']og:image[""']", RegexOptions.IgnoreCase);
        if (og.Success) return og.Groups[1].Value.Trim();

        var img = Regex.Match(html, @"<img[^>]+src=[""']([^""']+cover[^""']*)[""']", RegexOptions.IgnoreCase);
        if (img.Success)
        {
            string src = img.Groups[1].Value.Trim();
            return src.StartsWith("http") ? src : new Uri(new Uri(baseUrl), src).ToString();
        }
        return null;
    }
}
