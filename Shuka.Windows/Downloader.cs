using System.Text.RegularExpressions;
using Shuka.Core;

namespace Shuka;

/// <summary>
/// Orchestrates fetching, translating, and packaging a novel into an EPUB.
/// </summary>
internal sealed class Downloader
{
    private readonly PlaywrightFetcher _fetcher;
    private readonly WindowsTranslator _translator;
    private readonly HttpClient        _http;

    public WindowsTranslator Translator => _translator;

    public Downloader(PlaywrightFetcher fetcher, WindowsTranslator translator, HttpClient http)
    {
        _fetcher    = fetcher;
        _translator = translator;
        _http       = http;
    }

    /// <summary>Set after TUI-mode ProcessBookAsync if any chapters failed to translate.</summary>
    public string? LastTranslationLogPath   { get; private set; }
    public List<int> LastTranslationFailures { get; private set; } = new();
    public int     LastTranslationFailCount => LastTranslationFailures.Count;

    private List<(int Idx, string Title, string Text)>? _lastChapters;
    private BookInfo? _lastBook;
    private byte[]? _lastCoverBytes;
    private string? _lastCoverMime;
    private int? _lastLockedFromChapter;
    private string? _lastOutFile;

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

        var (chapters, lockedFromChapter, translationFailures) = await DownloadChaptersAsync(book, null, translate);

        _lastChapters          = chapters;
        _lastBook              = book;
        _lastCoverBytes        = coverBytes;
        _lastCoverMime         = coverMime;
        _lastLockedFromChapter = lockedFromChapter;
        _lastOutFile           = outFile;
        LastTranslationFailures = translationFailures;

        Console.WriteLine("\n  Building EPUB...");
        string path = BuildOutputPath(book, outFile);
        if (File.Exists(path)) File.Delete(path);
        EpubBuilder.Build(path, book.Title, book.TitleEn!, book.Author, book.AuthorEn!,
            chapters, coverBytes, coverMime, translate, lockedFromChapter);
        
        string cacheDir = Path.Combine(Path.GetTempPath(), "ShukaCache");
        string checkpointPath = CheckpointService.GetCheckpointPath(cacheDir, book.IndexUrl);
        if (translationFailures.Count == 0)
        {
            CheckpointService.Delete(checkpointPath);
        }

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

        // Cache book metadata so export-from-checkpoint skips re-fetching and re-translating
        string cacheDir0 = Path.Combine(Path.GetTempPath(), "ShukaCache");
        Directory.CreateDirectory(cacheDir0);
        string checkpointPath0 = CheckpointService.GetCheckpointPath(cacheDir0, book.IndexUrl);
        await CheckpointService.SaveBookMetaAsync(checkpointPath0, book.IndexUrl,
            new CheckpointService.BookMeta(
                book.Title,   book.TitleEn!,
                book.Author,  book.AuthorEn!,
                book.CoverUrl));

        ct.ThrowIfCancellationRequested();
        var (chapters, lockedFromChapter, translationFailures) = await DownloadChaptersAsync(book, onProgress, translate, ct);

        _lastChapters          = chapters;
        _lastBook              = book;
        _lastCoverBytes        = coverBytes;
        _lastCoverMime         = coverMime;
        _lastLockedFromChapter = lockedFromChapter;
        _lastOutFile           = outFile;
        LastTranslationFailures = translationFailures;

        ct.ThrowIfCancellationRequested();
        string path = BuildOutputPath(book, outFile);
        if (File.Exists(path)) File.Delete(path);
        EpubBuilder.Build(path, book.Title, book.TitleEn!, book.Author, book.AuthorEn!,
            chapters, coverBytes, coverMime, translate, lockedFromChapter);

        string cacheDir = Path.Combine(Path.GetTempPath(), "ShukaCache");
        string checkpointPath = CheckpointService.GetCheckpointPath(cacheDir, book.IndexUrl);
        if (translationFailures.Count == 0)
        {
            CheckpointService.Delete(checkpointPath);
        }

        // Write a translation error log only when failures occurred
        if (translationFailures.Count > 0)
        {
            string logDir  = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Shuka");
            Directory.CreateDirectory(logDir);
            string logPath = Path.Combine(logDir, "translation-errors.log");

            var lines = new List<string>
            {
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Translation failures for: {book.IndexUrl}",
                $"  Title : {book.TitleEn ?? book.Title}",
                $"  Failed chapters ({translationFailures.Count}): {string.Join(", ", translationFailures)}",
                ""
            };
            await File.AppendAllLinesAsync(logPath, lines);

            // Surface the log path so the TUI can display it after download
            LastTranslationLogPath = logPath;
        }
        else
        {
            LastTranslationLogPath = null;
        }
    }

    /// <summary>
    /// Retranslates chapters that failed in the previous download run, updates the checkpoint,
    /// and rebuilds the final EPUB.
    /// </summary>
    public async Task<(int resolvedCount, int remainingFailures)> RetranslateFailedChaptersAsync(
        Action<int, int, string>? onProgress = null,
        CancellationToken ct = default)
    {
        if (_lastBook == null || _lastChapters == null || LastTranslationFailures.Count == 0)
            return (0, 0);

        var book = _lastBook;
        var chapters = _lastChapters;
        var stillFailed = new List<int>();
        int resolved = 0;

        string cacheDir = Path.Combine(Path.GetTempPath(), "ShukaCache");
        string checkpointPath = CheckpointService.GetCheckpointPath(cacheDir, book.IndexUrl);
        var writeLock = new SemaphoreSlim(1, 1);

        for (int f = 0; f < LastTranslationFailures.Count; f++)
        {
            ct.ThrowIfCancellationRequested();
            int chNum = LastTranslationFailures[f]; // 1-based chapter number
            int chIdx = chNum - 1; // 0-based index

            if (chIdx < 0 || chIdx >= chapters.Count || chIdx >= book.ChapterUrls.Count)
                continue;

            var chRef = book.ChapterUrls[chIdx];
            onProgress?.Invoke(f + 1, LastTranslationFailures.Count, $"Retranslating ch {chNum} ({f + 1}/{LastTranslationFailures.Count})...");

            string existingText = chapters[chIdx].Text;
            const string Notice = "[Translation unavailable — original text below]\n";
            string rawChinese = existingText.StartsWith(Notice)
                ? existingText[Notice.Length..]
                : existingText;

            if (string.IsNullOrWhiteSpace(rawChinese))
            {
                try
                {
                    string html = await _fetcher.FetchAsync(chRef.Url, ct: ct);
                    var paras = book.Adapter.ExtractChapterText(html);
                    rawChinese = string.Join("\n", paras);
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    stillFailed.Add(chNum);
                    continue;
                }
            }

            if (string.IsNullOrWhiteSpace(rawChinese))
            {
                stillFailed.Add(chNum);
                continue;
            }

            const string TitleSep = "\n===TITLE===\n";
            string combined = chRef.Title + TitleSep + rawChinese;

            string translated;
            bool failed = false;
            try
            {
                translated = await _translator.Translate(combined, ct: ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (LocalAiNotInstalledException) { throw; }
            catch
            {
                translated = combined;
                failed = true;
            }

            string finalTitle = chRef.Title;
            string finalContent;

            int sepIdx = translated.IndexOf("===TITLE===", StringComparison.OrdinalIgnoreCase);
            if (sepIdx >= 0)
            {
                finalTitle   = translated[..sepIdx].Trim();
                finalContent = translated[(sepIdx + "===TITLE===".Length)..].Trim();
            }
            else
            {
                finalContent = translated;
            }

            if (!failed)
            {
                failed = WindowsTranslator.IsUntranslatedChinese(combined, translated);
            }

            if (failed)
            {
                stillFailed.Add(chNum);
                finalContent = Notice + rawChinese;
            }
            else
            {
                resolved++;
            }

            chapters[chIdx] = (chNum, finalTitle, finalContent);

            await CheckpointService.SaveChapterAsync(checkpointPath, book.IndexUrl, chIdx, finalTitle, finalContent, writeLock);
        }

        LastTranslationFailures = stillFailed;

        // Rebuild EPUB with updated chapters
        string path = BuildOutputPath(book, _lastOutFile);
        if (File.Exists(path)) File.Delete(path);
        EpubBuilder.Build(path, book.Title, book.TitleEn!, book.Author, book.AuthorEn!,
            chapters, _lastCoverBytes, _lastCoverMime ?? "image/jpeg", true, _lastLockedFromChapter);

        if (stillFailed.Count == 0)
        {
            CheckpointService.Delete(checkpointPath);
        }

        return (resolved, stillFailed.Count);
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

        // Try to use cached metadata — avoids re-fetching the index page and re-translating
        var cachedMeta = await CheckpointService.LoadBookMetaAsync(checkpointPath);

        string title, titleEn, author, authorEn;
        string? coverUrl;

        if (cachedMeta != null)
        {
            title    = cachedMeta.Title;
            titleEn  = cachedMeta.TitleEn;
            author   = cachedMeta.Author;
            authorEn = cachedMeta.AuthorEn;
            coverUrl = cachedMeta.CoverUrl;
        }
        else
        {
            Console.WriteLine("  Gathering book info...");
            var book = await GatherBookInfoAsync(indexUrl);

            if (translate)
            {
                Console.Write("  Translating title/author...");
                book.TitleEn  = await _translator.Translate(book.Title);
                book.AuthorEn = await _translator.Translate(book.Author);
                Console.WriteLine(" done");
            }
            else
            {
                book.TitleEn  = book.Title;
                book.AuthorEn = book.Author;
            }

            title    = book.Title;
            titleEn  = book.TitleEn!;
            author   = book.Author;
            authorEn = book.AuthorEn!;
            coverUrl = book.CoverUrl;
        }

        byte[]? coverBytes;
        string coverMime;
        var coverRes = await DownloadCoverAsync(coverUrl);
        coverBytes = coverRes.bytes;
        coverMime  = coverRes.mime;

        Console.WriteLine($"\n  Building Sample EPUB ({savedChapters.Count} chapters)...");
        string path = BuildSampleOutputPath(title, titleEn, outFile, savedChapters.Count);
        if (File.Exists(path)) File.Delete(path);

        EpubBuilder.Build(path, title, titleEn, author, authorEn,
            savedChapters, coverBytes, coverMime, translate);

        Console.WriteLine($"  Sample EPUB saved: {Path.GetFullPath(path)}");
        return path;
    }

    // ── Chapter download pipeline ─────────────────────────────────────────────

    private async Task<(List<(int Idx, string Title, string Text)> chapters, int? lockedFromChapter, List<int> translationFailures)> DownloadChaptersAsync(
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
        var translationFailures = new List<int>();

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
                    if (book.Adapter.StopOnFirstLockedChapter)
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

            string html;
            try
            {
                html = await _fetcher.FetchAsync(ch.Url, ct: ct);
            }
            catch (CloudflareBlockedException cfEx)
            {
                // Site is still behind CF — abort the whole download with a clear message
                // (don't silently treat chapters as locked)
                throw new Exception(
                    $"Download stopped: {cfEx.Message}\n" +
                    "  → Return to the main menu, choose 'Fix Cloudflare (--solve-cf)', solve the challenge, then retry.",
                    cfEx);
            }
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
                if (book.Adapter.StopOnFirstLockedChapter)
                    lockedFromChapter = firstLockedChapterNumber;
                string statusMsg = $"Chapter {firstLockedChapterNumber} is locked in {book.Adapter.SiteName}. Finished download at chapter {lastUnlocked}.";
                Console.WriteLine($"\n  [Notice] {statusMsg}");
                onProgress?.Invoke(lastUnlocked, book.Total, statusMsg);
                break;
            }

            string content;
            bool translationFailed = false;
            if (isLocked)
            {
                content = string.Empty;
                onProgress?.Invoke(i + 1, book.Total, $"[locked] ch {i + 1} of {book.Total}");
            }
            else if (translate)
            {
                // Translate title and body together using a sentinel separator so we get
                // an English chapter title for the EPUB heading and TOC, not just the body.
                const string TitleSep = "\n===TITLE===\n";
                string combined = ch.Title + TitleSep + string.Join("\n", paras);

                string translated;
                try
                {
                    translated = await _translator.Translate(combined, ct: ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (LocalAiNotInstalledException) { throw; }
                catch
                {
                    translated = combined; // treat as failure, handled below
                    translationFailed = true;
                }

                int sepIdx = translated.IndexOf("===TITLE===", StringComparison.OrdinalIgnoreCase);
                if (sepIdx >= 0)
                {
                    ch = new ChapterRef(ch.Url, translated[..sepIdx].Trim());
                    content = translated[(sepIdx + "===TITLE===".Length)..].Trim();
                }
                else
                {
                    // Sentinel was dropped by the translator — keep original title, use full result as body
                    content = translated;
                }

                // Detect silent translation failure: result still contains a lot of Chinese
                if (!translationFailed)
                {
                    translationFailed = Regex.IsMatch(content, @"[\u4e00-\u9fff]{20,}");
                }

                if (translationFailed)
                {
                    translationFailures.Add(i + 1);
                    content = "[Translation unavailable — original text below]\n" + content;
                }
            }
            else
            {
                content = string.Join("\n", paras);
            }

            chapters.Add((i + 1, ch.Title, content));

            await CheckpointService.SaveChapterAsync(checkpointPath, book.IndexUrl, i, ch.Title, content, writeLock);

            if (!isLocked)
                onProgress?.Invoke(i + 1, book.Total, translate ? $"Chapter {i + 1} of {book.Total}" : $"Downloaded ch {i + 1} of {book.Total}");

            // ── Long download pacing (500+ chapters rate-limit prevention) ────────
            if (translate && (i + 1) < book.Total)
            {
                if ((i + 1) % 25 == 0)
                {
                    int pauseSec = _translator.IsGoogleRateLimited ? 4 : 2;
                    await Task.Delay(pauseSec * 1000, ct);
                }
                else
                {
                    int chDelayMs = _translator.IsGoogleRateLimited ? 800 : 250;
                    await Task.Delay(chDelayMs, ct);
                }
            }
        }

        return (chapters, lockedFromChapter, translationFailures);
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

    private static string BuildSampleOutputPath(string title, string titleEn, string? outFile, int chapterCount)
    {
        if (outFile != null) return outFile;
        string safeName = Regex.Replace(titleEn ?? title,
            @"[\\/:*?""<>|]", "_").Trim('_');
        if (string.IsNullOrWhiteSpace(safeName))
            safeName = "unknown";
        string fileName = safeName[..Math.Min(safeName.Length, 50)] + $"_sample_ch1-{chapterCount}.epub";
        string downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Path.Combine(downloads, fileName);
    }
}
