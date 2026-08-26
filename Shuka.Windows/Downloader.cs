using System.Text.RegularExpressions;
using Shuka.Core;

namespace Shuka;

/// <summary>
/// Orchestrates fetching, translating, and packaging a novel into an EPUB.
/// </summary>
internal sealed class Downloader
{
    private readonly PlaywrightFetcher _fetcher;
    private readonly Translator        _translator;
    private readonly HttpClient        _http;

    public Downloader(PlaywrightFetcher fetcher, Translator translator, HttpClient http)
    {
        _fetcher    = fetcher;
        _translator = translator;
        _http       = http;
    }

    // ── Book info ─────────────────────────────────────────────────────────────

    public async Task<BookInfo> GatherBookInfoAsync(
        string indexUrl, int chapterLimit = 0, string? forceCoverUrl = null,
        int chapterFrom = 0)
    {
        var adapter = DetectAdapter(indexUrl);
        indexUrl = adapter.NormalizeUrl(indexUrl);
        Console.WriteLine($"  Gathering [{adapter.SiteName}]: {indexUrl}");

        string html = await _fetcher.FetchAsync(indexUrl);
        var info    = adapter.ParseIndex(html, indexUrl);

        int from = chapterFrom > 0 ? chapterFrom - 1 : 0;
        var rangedUrls = chapterLimit > 0
            ? info.ChapterUrls.Skip(from).Take(chapterLimit).ToList()
            : info.ChapterUrls.Skip(from).ToList();

        int total    = rangedUrls.Count;
        string? coverUrl = forceCoverUrl ?? info.CoverUrl ?? TryExtractCover(html, indexUrl);

        return new BookInfo(indexUrl, info.Title, info.Author,
            rangedUrls, total, chapterLimit, coverUrl, adapter)
        {
            ChapterFrom = chapterFrom
        };
    }

    // ── Full pipeline ─────────────────────────────────────────────────────────

    /// <summary>CLI mode — prints progress to Console.</summary>
    public async Task ProcessBookAsync(BookInfo book, string? outFile = null, bool translate = true)
    {
        Console.WriteLine($"\n--- {book.Title} ({book.Total} chapters) [{book.Adapter.SiteName}] ---");

        byte[]? coverBytes;
        string coverMime;

        if (translate)
        {
            Console.Write("  Translating title/author...");
            book.TitleEn  = await _translator.Translate(book.Title);
            book.AuthorEn = await _translator.Translate(book.Author);
            Console.WriteLine(" done");
            Console.WriteLine($"  Title (EN):  {book.TitleEn}");
            Console.WriteLine($"  Author (EN): {book.AuthorEn}");

            var coverRes = await DownloadCoverAsync(book.CoverUrl);
            coverBytes = coverRes.bytes;
            coverMime = coverRes.mime;
        }
        else
        {
            Console.WriteLine("  Processing title/author...");
            book.TitleEn  = book.Title;
            book.AuthorEn = book.Author;

            var coverRes = await DownloadCoverAsync(book.CoverUrl);
            coverBytes = coverRes.bytes;
            coverMime = coverRes.mime;
        }

        var (chapters, lockedFromChapter) = await DownloadChaptersAsync(book, null, translate);

        Console.WriteLine("\n  Building EPUB...");
        string path = BuildOutputPath(book, outFile);
        if (File.Exists(path)) File.Delete(path);
        EpubBuilder.Build(path, book.Title, book.TitleEn!, book.Author, book.AuthorEn!,
            chapters, coverBytes, coverMime, translate, lockedFromChapter);
        
        string cacheDir = Path.Combine(Path.GetTempPath(), "ShukaCache");
        string checkpointPath = CheckpointService.GetCheckpointPath(cacheDir, book.IndexUrl);
        CheckpointService.Delete(checkpointPath);

        Console.WriteLine($"  Saved: {Path.GetFullPath(path)}");
    }

    /// <summary>TUI mode — reports progress via callback instead of Console.Write.</summary>
    public async Task ProcessBookAsync(BookInfo book, string? outFile,
        Action<int, int, string> onProgress, bool translate = true,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        byte[]? coverBytes;
        string coverMime;

        if (translate)
        {
            book.TitleEn  = await _translator.Translate(book.Title);
            book.AuthorEn = await _translator.Translate(book.Author);
            var coverRes = await DownloadCoverAsync(book.CoverUrl, silent: true);
            coverBytes = coverRes.bytes;
            coverMime = coverRes.mime;
        }
        else
        {
            book.TitleEn  = book.Title;
            book.AuthorEn = book.Author;
            var coverRes = await DownloadCoverAsync(book.CoverUrl, silent: true);
            coverBytes = coverRes.bytes;
            coverMime = coverRes.mime;
        }

        ct.ThrowIfCancellationRequested();
        var (chapters, lockedFromChapter) = await DownloadChaptersAsync(book, onProgress, translate, ct);

        ct.ThrowIfCancellationRequested();
        string path = BuildOutputPath(book, outFile);
        if (File.Exists(path)) File.Delete(path);
        EpubBuilder.Build(path, book.Title, book.TitleEn!, book.Author, book.AuthorEn!,
            chapters, coverBytes, coverMime, translate, lockedFromChapter);

        string cacheDir = Path.Combine(Path.GetTempPath(), "ShukaCache");
        string checkpointPath = CheckpointService.GetCheckpointPath(cacheDir, book.IndexUrl);
        CheckpointService.Delete(checkpointPath);
    }

    /// <summary>Generates a sample EPUB from already downloaded/checkpointed chapters.</summary>
    public async Task<string?> GenerateSampleEpubAsync(string indexUrl, string? outFile = null, bool translate = true)
    {
        string cacheDir = Path.Combine(Path.GetTempPath(), "ShukaCache");
        string checkpointPath = CheckpointService.GetCheckpointPath(cacheDir, indexUrl);

        int savedCount = CheckpointService.CountSaved(checkpointPath);
        if (savedCount == 0)
        {
            Console.WriteLine($"  [Notice] No saved checkpoint chapters found for: {indexUrl}");
            return null;
        }

        Console.WriteLine($"  Found {savedCount} checkpoint chapter(s) for sample EPUB generation...");
        var savedChapters = await CheckpointService.LoadAllSavedChaptersAsync(checkpointPath);
        if (savedChapters.Count == 0)
        {
            Console.WriteLine("  [Error] Checkpoint contains no valid chapter data.");
            return null;
        }

        Console.WriteLine("  Gathering book info...");
        var book = await GatherBookInfoAsync(indexUrl);

        byte[]? coverBytes;
        string coverMime;
        if (translate)
        {
            Console.Write("  Translating title/author...");
            book.TitleEn = await _translator.Translate(book.Title);
            book.AuthorEn = await _translator.Translate(book.Author);
            Console.WriteLine(" done");
            var coverRes = await DownloadCoverAsync(book.CoverUrl);
            coverBytes = coverRes.bytes;
            coverMime = coverRes.mime;
        }
        else
        {
            book.TitleEn = book.Title;
            book.AuthorEn = book.Author;
            var coverRes = await DownloadCoverAsync(book.CoverUrl);
            coverBytes = coverRes.bytes;
            coverMime = coverRes.mime;
        }

        Console.WriteLine($"\n  Building Sample EPUB ({savedChapters.Count} chapters)...");
        string path = BuildSampleOutputPath(book, outFile, savedChapters.Count);
        if (File.Exists(path)) File.Delete(path);

        EpubBuilder.Build(path, book.Title, book.TitleEn!, book.Author, book.AuthorEn!,
            savedChapters, coverBytes, coverMime, translate);

        Console.WriteLine($"  Sample EPUB saved: {Path.GetFullPath(path)}");
        return path;
    }

    // ── Chapter download pipeline ─────────────────────────────────────────────

    private async Task<(List<(int Idx, string Title, string Text)> chapters, int? lockedFromChapter)> DownloadChaptersAsync(
        BookInfo book, Action<int, int, string>? onProgress, bool translate = true,
        CancellationToken ct = default)
    {
        string cacheDir = Path.Combine(Path.GetTempPath(), "ShukaCache");
        Directory.CreateDirectory(cacheDir);
        string checkpointPath = CheckpointService.GetCheckpointPath(cacheDir, book.IndexUrl);
        var writeLock = new SemaphoreSlim(1, 1);

        var saved = await CheckpointService.LoadAsync(checkpointPath, book.Total);
        int alreadyDone = saved.Count(r => r != null);

        var chapters = new List<(int Idx, string Title, string Text)>(book.Total);
        int? lockedFromChapter = null;

        var t0 = DateTime.Now;
        int consecutiveLockedCount = 0;
        int firstLockedChapterNumber = 0;

        for (int i = 0; i < book.Total; i++)
        {
            // Check pause/cancel between every chapter — checkpoint is already saved
            ct.ThrowIfCancellationRequested();
            var ch = book.ChapterUrls[i];

            if (saved[i] != null)
            {
                string savedTitle = saved[i]!.Value.title;
                string savedText  = saved[i]!.Value.text;

                if (string.IsNullOrWhiteSpace(savedText))
                {
                    consecutiveLockedCount++;
                    if (consecutiveLockedCount == 1) firstLockedChapterNumber = i + 1;
                }
                else
                {
                    consecutiveLockedCount = 0;
                }

                if (consecutiveLockedCount >= 3)
                {
                    int lastUnlocked = firstLockedChapterNumber - 1;
                    int removeCount = consecutiveLockedCount - 1;
                    if (chapters.Count >= removeCount)
                    {
                        chapters.RemoveRange(chapters.Count - removeCount, removeCount);
                    }
                    lockedFromChapter = firstLockedChapterNumber;
                    string statusMsg = $"Chapter {firstLockedChapterNumber} is locked in {book.Adapter.SiteName}. Finished download at chapter {lastUnlocked}.";
                    Console.WriteLine($"\n  [Notice] {statusMsg}");
                    onProgress?.Invoke(lastUnlocked, book.Total, statusMsg);
                    break;
                }

                chapters.Add((i + 1, savedTitle, savedText));
                onProgress?.Invoke(i + 1, book.Total, translate ? $"Chapter {i + 1} of {book.Total}" : $"Downloaded ch {i + 1} of {book.Total}");
                continue;
            }

            if (onProgress == null)
            {
                double elapsed = (DateTime.Now - t0).TotalSeconds;
                string eta = i > 0
                    ? $"~{TimeSpan.FromSeconds(elapsed / i * (book.Total - i)):mm\\:ss} left"
                    : "";
                string phaseText = translate ? "Translating" : "Downloading";
                Console.Write($"\r  [{i + 1}/{book.Total}] {phaseText}... {eta}      ");
            }

            string html = await _fetcher.FetchAsync(ch.Url);
            var paras = book.Adapter.ExtractChapterText(html);

            // Empty paragraphs = chapter is paywalled / locked
            bool isLocked = paras.Count == 0;

            if (isLocked)
            {
                consecutiveLockedCount++;
                if (consecutiveLockedCount == 1) firstLockedChapterNumber = i + 1;
            }
            else
            {
                consecutiveLockedCount = 0;
            }

            if (consecutiveLockedCount >= 3)
            {
                int lastUnlocked = firstLockedChapterNumber - 1;
                int removeCount = consecutiveLockedCount - 1;
                if (chapters.Count >= removeCount)
                {
                    chapters.RemoveRange(chapters.Count - removeCount, removeCount);
                }
                lockedFromChapter = firstLockedChapterNumber;
                string statusMsg = $"Chapter {firstLockedChapterNumber} is locked in {book.Adapter.SiteName}. Finished download at chapter {lastUnlocked}.";
                Console.WriteLine($"\n  [Notice] {statusMsg}");
                onProgress?.Invoke(lastUnlocked, book.Total, statusMsg);
                break;
            }

            string content;
            if (isLocked)
            {
                content = string.Empty;
                onProgress?.Invoke(i + 1, book.Total, $"[locked] ch {i + 1} of {book.Total}");
            }
            else if (translate)
            {
                content = await _translator.Translate(string.Join("\n", paras));
            }
            else
            {
                content = string.Join("\n", paras);
            }

            chapters.Add((i + 1, ch.Title, content));

            await CheckpointService.SaveChapterAsync(checkpointPath, book.IndexUrl, i, ch.Title, content, writeLock);

            if (!isLocked)
                onProgress?.Invoke(i + 1, book.Total, translate ? $"Chapter {i + 1} of {book.Total}" : $"Downloaded ch {i + 1} of {book.Total}");
        }

        return (chapters, lockedFromChapter);
    }

    // ── Cover download ────────────────────────────────────────────────────────

    private async Task<(byte[]? bytes, string mime)> DownloadCoverAsync(
        string? coverUrl, bool silent = false)
    {
        if (string.IsNullOrWhiteSpace(coverUrl)) return (null, "image/jpeg");
        if (!silent) Console.Write("  Downloading cover...");
        try
        {
            byte[] bytes = await _http.GetByteArrayAsync(coverUrl);
            string mime  = "image/jpeg";
            if (bytes.Length >= 4)
            {
                if (bytes[0] == 0x89 && bytes[1] == 0x50) mime = "image/png";
                else if (bytes[0] == 0xFF && bytes[1] == 0xD8) mime = "image/jpeg";
                else if (bytes[0] == 0x47 && bytes[1] == 0x49) mime = "image/gif";
            }
            if (!silent) Console.WriteLine($" OK ({bytes.Length / 1024}KB, {mime})");
            return (bytes, mime);
        }
        catch (Exception ex)
        {
            if (!silent) Console.WriteLine($" Failed: {ex.Message} (using generated cover)");
            return (null, "image/jpeg");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ISiteAdapter DetectAdapter(string url)
    {
        ISiteAdapter[] adapters =
        [
            new Shuka.Core.Adapters.ShukuAdapter(),
            new Shuka.Core.Adapters.CzBooksAdapter(),
            new Shuka.Core.Adapters.DmxsAdapter(),
            new Shuka.Core.Adapters.ShubaAdapter(),
            new Shuka.Core.Adapters.QuanbenAdapter(),
            new Shuka.Core.Adapters.SituuAdapter(),
            new Shuka.Core.Adapters.YamiboAdapter(),
            new Shuka.Core.Adapters.ZhenhunAdapter(),
            new Shuka.Core.Adapters.NoveldexAdapter(),
            new Shuka.Core.Adapters.ShubaowAdapter(),
        ];
        return adapters.FirstOrDefault(a => a.Matches(url))
            ?? throw new Exception(
                $"No supported adapter for URL: {url}\n" +
                "Supported sites: 52shuku.net, czbooks.net, dmxs.org, 69shuba.com, quanben.io, situu.cc, yamibo.com, zhenhunxiaoshuo.com, noveldex.io, shubaow.net");
    }

    private static string? TryExtractCover(string html, string baseUrl)
    {
        var og = Regex.Match(html,
            @"<meta[^>]+property=[""']og:image[""'][^>]+content=[""']([^""']+)[""']",
            RegexOptions.IgnoreCase);
        if (!og.Success)
            og = Regex.Match(html,
                @"<meta[^>]+content=[""']([^""']+)[""'][^>]+property=[""']og:image[""']",
                RegexOptions.IgnoreCase);
        if (og.Success) return og.Groups[1].Value.Trim();

        var img = Regex.Match(html,
            @"<img[^>]+src=[""']([^""']+cover[^""']*)[""']",
            RegexOptions.IgnoreCase);
        if (img.Success)
        {
            string src = img.Groups[1].Value.Trim();
            return src.StartsWith("http") ? src : new Uri(new Uri(baseUrl), src).ToString();
        }
        return null;
    }

    private static string BuildOutputPath(BookInfo book, string? outFile)
    {
        if (outFile != null) return outFile;
        string safeName = Regex.Replace(book.TitleEn ?? book.Title,
            @"[\\/:*?""<>|]", "_").Trim('_');
        if (string.IsNullOrWhiteSpace(safeName))
            safeName = Regex.Match(book.IndexUrl, @"/([^/]+?)/?$").Groups[1].Value;
        string fileName = safeName[..Math.Min(safeName.Length, 60)] +
                          (book.ChapterLimit > 0 ? $"_ch1-{book.Total}" : "") + ".epub";
        string downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Path.Combine(downloads, fileName);
    }

    private static string BuildSampleOutputPath(BookInfo book, string? outFile, int chapterCount)
    {
        if (outFile != null) return outFile;
        string safeName = Regex.Replace(book.TitleEn ?? book.Title,
            @"[\\/:*?""<>|]", "_").Trim('_');
        if (string.IsNullOrWhiteSpace(safeName))
            safeName = Regex.Match(book.IndexUrl, @"/([^/]+?)/?$").Groups[1].Value;
        string fileName = safeName[..Math.Min(safeName.Length, 50)] + $"_sample_ch1-{chapterCount}.epub";
        string downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Path.Combine(downloads, fileName);
    }
}
