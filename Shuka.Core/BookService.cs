using System.Text.RegularExpressions;
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
        string? forceCoverUrl = null, Action<string>? log = null)
    {
        var adapter = DetectAdapter(indexUrl);
        indexUrl = adapter.NormalizeUrl(indexUrl);
        log?.Invoke($"Gathering [{adapter.SiteName}]: {indexUrl}");

        string html = await _fetcher.Fetch(indexUrl, log: log);
        var info = adapter.ParseIndex(html, indexUrl);
        int total = chapterLimit > 0 ? Math.Min(chapterLimit, info.ChapterUrls.Count) : info.ChapterUrls.Count;
        string? coverUrl = forceCoverUrl ?? info.CoverUrl ?? TryExtractCover(html, indexUrl);

        return new BookInfo(indexUrl, info.Title, info.Author, info.ChapterUrls, total, chapterLimit, coverUrl, adapter);
    }

    public async Task<string> ProcessBook(BookInfo book, string outputPath,
        IProgress<ProgressEventArgs>? progress = null, Action<string>? log = null)
    {
        log?.Invoke($"Translating title/author...");
        book.TitleEn  = await _translator.Translate(book.Title,  log);
        book.AuthorEn = await _translator.Translate(book.Author, log);
        log?.Invoke($"Title (EN): {book.TitleEn}  Author (EN): {book.AuthorEn}");

        var (coverBytes, coverMime) = await DownloadCover(book.CoverUrl, log);
        var chapters = await DownloadChapters(book, progress, log);

        log?.Invoke("Building EPUB...");
        if (File.Exists(outputPath)) File.Delete(outputPath);
        EpubBuilder.Build(outputPath, book.Title, book.TitleEn!, book.Author, book.AuthorEn!,
            chapters, coverBytes, coverMime);

        return outputPath;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<List<(int Idx, string Title, string Text)>> DownloadChapters(
        BookInfo book, IProgress<ProgressEventArgs>? progress, Action<string>? log)
    {
        var fetchSem = new SemaphoreSlim(3);

        var fetchTasks = book.ChapterUrls.Take(book.Total).Select(async (ch, i) =>
        {
            await fetchSem.WaitAsync();
            try   { return (i, title: ch.Title, html: await _fetcher.Fetch(ch.Url, log: log)); }
            finally { fetchSem.Release(); }
        }).ToArray();

        var chapters = new List<(int Idx, string Title, string Text)>(book.Total);

        for (int i = 0; i < book.Total; i++)
        {
            progress?.Report(new ProgressEventArgs
            {
                Current = i + 1,
                Total   = book.Total,
                Message = $"Translating chapter {i + 1} of {book.Total}..."
            });

            var (_, chTitle, html) = await fetchTasks[i];
            var paras = book.Adapter.ExtractChapterText(html);
            string english = await _translator.Translate(string.Join("\n", paras), log);
            chapters.Add((i + 1, chTitle, english));
        }

        return chapters;
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
