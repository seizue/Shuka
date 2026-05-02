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
        [new ShukuAdapter(), new CzBooksAdapter(), new DmxsAdapter()];

    public BookService(ICloudflareBypass? cfBypass = null)
    {
        _fetcher = new HttpFetcher(cfBypass);

        var gh = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3
        };
        _gtClient = new HttpClient(gh) { Timeout = TimeSpan.FromSeconds(30) };
        _gtClient.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Linux; Android 10; Mobile) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Mobile Safari/537.36");
        _gtClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9,zh-CN;q=0.8,zh;q=0.7");

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
        log?.Invoke("Translating title/author...");

        // Run title/author translation and cover download in parallel
        var titleTask  = _translator.Translate(book.Title,  log, ct);
        var authorTask = _translator.Translate(book.Author, log, ct);
        var coverTask  = DownloadCover(book.CoverUrl, log);

        await Task.WhenAll(titleTask, authorTask, coverTask);

        book.TitleEn  = titleTask.Result;
        book.AuthorEn = authorTask.Result;
        var (coverBytes, coverMime) = coverTask.Result;

        log?.Invoke($"Title (EN): {book.TitleEn}  Author (EN): {book.AuthorEn}");

        ct.ThrowIfCancellationRequested();
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
    /// Fetches up to 8 chapters concurrently; translates up to 6 concurrently.
    /// Translation starts as soon as a chapter's HTML arrives.
    /// Results are assembled in original order.
    /// </summary>
    private async Task<List<(int Idx, string Title, string Text)>> DownloadChapters(
        BookInfo book, IProgress<ProgressEventArgs>? progress, Action<string>? log,
        CancellationToken ct = default)
    {
        var chapterList = book.ChapterUrls.Take(book.Total).ToList();
        int total = chapterList.Count;

        // Channel buffer = 3× fetch concurrency so translators are never starved
        var channel = Channel.CreateBounded<(int i, string title, string html)>(
            new BoundedChannelOptions(30)
            {
                SingleWriter = false,
                SingleReader = false,
                FullMode     = BoundedChannelFullMode.Wait
            });

        // 12 concurrent fetches — more parallelism; CF-protected sites
        // are serialised anyway by the WebView bypass.
        var fetchSem = new SemaphoreSlim(12);

        // ── Stage 1: fetch all chapters → channel ─────────────────────────────
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

        // ── Stage 2: translate in parallel, store results ─────────────────────
        var results   = new (string title, string text)?[total];
        int completed = 0;

        // 12 consumer workers — matches the global translate semaphore in Translator
        var translateTasks = Enumerable.Range(0, 12).Select(_ => Task.Run(async () =>
        {
            await foreach (var (i, chTitle, html) in channel.Reader.ReadAllAsync(ct))
            {
                var paras   = book.Adapter.ExtractChapterText(html);
                string text = await _translator.Translate(
                    string.Join("\n", paras), log, ct);
                results[i] = (chTitle, text);

                int done = Interlocked.Increment(ref completed);
                progress?.Report(new ProgressEventArgs
                {
                    Current = done,
                    Total   = total,
                    Message = $"Translated chapter {done} of {total}..."
                });
            }
        }, ct)).ToArray();

        await Task.WhenAll(fetchProducer);
        await Task.WhenAll(translateTasks);

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
        ?? throw new Exception($"No supported adapter for URL: {url}\nSupported: 52shuku.net, czbooks.net, dmxs.org");

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
