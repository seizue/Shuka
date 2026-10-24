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
    private readonly HttpClient _gtClient;
    private readonly Translator _translator;

    /// <summary>Chapter numbers (1-based) that could not be translated due to rate limits or network errors.</summary>
    public List<int> LastTranslationFailures { get; } = new();

    public List<(int Idx, string Title, string Text)>? LastChapters { get; private set; }
    public BookInfo? LastBook { get; private set; }
    public byte[]? LastCoverBytes { get; private set; }
    public string? LastCoverMime { get; private set; }
    public int? LastLockedFromChapter { get; private set; }
    public string? LastOutputPath { get; private set; }

    public static readonly ISiteAdapter[] Adapters =
        [new ShukuAdapter(), new CzBooksAdapter(), new DmxsAdapter(), new ShubaAdapter(), new QuanbenAdapter(), new SituuAdapter(), new YamiboAdapter(), new ZhenhunAdapter(), new NoveldexAdapter(), new ShubaowAdapter()];

    /// <summary>English-only sources (noveldex.io) should never be translated during download.</summary>
    public static bool ShouldTranslate(string url, bool translate) =>
        translate && !NoveldexAdapter.IsNoveldexUrl(url);

    /// <summary>
    /// Upgrades <c>http://</c> to <c>https://</c> when the URL matches a known reader site.
    /// Does not change paths: <see cref="ISiteAdapter.NormalizeUrl"/> is for the download pipeline and often
    /// redirects chapter URLs to the book index, which would be wrong while browsing.
    /// </summary>
    public static string EnsureHttpsIfKnownReaderSite(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return url;

        string t = url.Trim();
        if (!t.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return url;

        foreach (var a in Adapters)
        {
            if (a.Matches(t))
                return "https://" + t[7..];
        }

        return url;
    }

    public BookService(ICloudflareBypass? cfBypass = null)
    {
        _fetcher = new HttpFetcher(cfBypass);

        var gh = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3
        };
        _gtClient = new HttpClient(gh) { Timeout = TimeSpan.FromSeconds(60) };
        _gtClient.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Linux; Android 10; Mobile) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Mobile Safari/537.36");
        _gtClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9,zh-CN;q=0.8,zh;q=0.7");

        _translator = new Translator(_gtClient);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task<BookInfo> GatherBookInfo(string indexUrl, int chapterLimit = 0,
        string? forceCoverUrl = null, Action<string>? log = null,
        CancellationToken ct = default, int chapterFrom = 0)
    {
        var adapter = DetectAdapter(indexUrl);
        indexUrl = adapter.NormalizeUrl(indexUrl);
        log?.Invoke($"Gathering [{adapter.SiteName}]: {indexUrl}");

        string html = await _fetcher.Fetch(indexUrl, log: log, ct: ct, forceBypass: adapter.RequiresCfBypass);
        var info = adapter.ParseIndex(html, indexUrl);

        // Apply from/to range
        // chapterFrom is 1-based (1 = first chapter); 0 means start from beginning
        int from = chapterFrom > 0 ? chapterFrom - 1 : 0; // convert to 0-based index
        var rangedUrls = chapterLimit > 0
            ? info.ChapterUrls.Skip(from).Take(chapterLimit).ToList()
            : info.ChapterUrls.Skip(from).ToList();

        int total = rangedUrls.Count;
        string? coverUrl = forceCoverUrl ?? info.CoverUrl ?? TryExtractCover(html, indexUrl);

        // If the adapter couldn't find a cover in the index page, try the hint URL
        // (e.g. QuanbenAdapter points to the non-AMP book info page that has the cover img)
        if (coverUrl == null && info.CoverHintUrl != null)
        {
            try
            {
                log?.Invoke($"Fetching cover hint page: {info.CoverHintUrl}");
                string hintHtml = await _fetcher.Fetch(info.CoverHintUrl, log: log, ct: ct,
                    forceBypass: adapter.RequiresCfBypass);
                coverUrl = TryExtractCoverFromHint(hintHtml)
                        ?? TryExtractCover(hintHtml, info.CoverHintUrl);
            }
            catch { /* cover is optional — silently ignore */ }
        }

        var book = new BookInfo(indexUrl, info.Title, info.Author,
            rangedUrls, total, chapterLimit, coverUrl, adapter)
        {
            ChapterFrom = chapterFrom
        };
        return book;
    }

    public async Task<string> ProcessBook(BookInfo book, string outputPath,
        IProgress<ProgressEventArgs>? progress = null, Action<string>? log = null,
        CancellationToken ct = default, string? checkpointPath = null, bool translate = true)
    {
        translate = ShouldTranslate(book.IndexUrl, translate);
        ct.ThrowIfCancellationRequested();
        byte[]? coverBytes;
        string coverMime;

        if (translate)
        {
            log?.Invoke("Translating title/author...");

            // Run title/author translation and cover download in parallel
            var titleTask = _translator.Translate(book.Title, log, ct);
            var authorTask = _translator.Translate(book.Author, log, ct);
            var coverTask = DownloadCover(book.CoverUrl, log, ct);

            try
            {
                await Task.WhenAll(titleTask, authorTask);
                book.TitleEn = titleTask.Result;
                book.AuthorEn = authorTask.Result;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[Title/Author translation failed: {ex.Message} — using original]");
                book.TitleEn = book.Title;
                book.AuthorEn = book.Author;
            }

            (coverBytes, coverMime) = await coverTask;

            log?.Invoke($"Title (EN): {book.TitleEn}  Author (EN): {book.AuthorEn}");
        }
        else
        {
            log?.Invoke("Processing title/author...");

            var coverTask = DownloadCover(book.CoverUrl, log, ct);
            await coverTask;

            book.TitleEn = book.Title;
            book.AuthorEn = book.Author;
            (coverBytes, coverMime) = coverTask.Result;
        }

        ct.ThrowIfCancellationRequested();
        var (chapters, lockedFromChapter) = await DownloadChapters(book, progress, log, ct, checkpointPath, translate);

        LastBook = book;
        LastChapters = chapters;
        LastCoverBytes = coverBytes;
        LastCoverMime = coverMime;
        LastLockedFromChapter = lockedFromChapter;
        LastOutputPath = outputPath;

        ct.ThrowIfCancellationRequested();
        log?.Invoke("Building EPUB...");
        if (File.Exists(outputPath)) File.Delete(outputPath);
        EpubBuilder.Build(outputPath, book.Title, book.TitleEn!, book.Author, book.AuthorEn!,
            chapters, coverBytes, coverMime, translate, lockedFromChapter);

        // Delete checkpoint on full success — keep it if chapters failed so user can retranslate
        if (checkpointPath != null && LastTranslationFailures.Count == 0)
            CheckpointService.Delete(checkpointPath);

        return outputPath;
    }

    /// <summary>
    /// Retranslates chapters that failed in the previous download run, updates the checkpoint,
    /// and rebuilds the final EPUB.
    /// </summary>
    public async Task<(int resolvedCount, int remainingFailures)> RetranslateFailedChaptersAsync(
        BookInfo? book = null,
        List<(int Idx, string Title, string Text)>? chapters = null,
        string? outputPath = null,
        string? checkpointPath = null,
        IProgress<ProgressEventArgs>? progress = null,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        book ??= LastBook;
        chapters ??= LastChapters;
        outputPath ??= LastOutputPath;

        if (book == null || chapters == null || LastTranslationFailures.Count == 0)
            return (0, 0);

        var stillFailed = new List<int>();
        int resolved = 0;
        var writeLock = new SemaphoreSlim(1, 1);
        int totalToRetry = LastTranslationFailures.Count;

        for (int f = 0; f < LastTranslationFailures.Count; f++)
        {
            ct.ThrowIfCancellationRequested();
            int chNum = LastTranslationFailures[f]; // 1-based
            int chIdx = chNum - 1; // 0-based

            if (chIdx < 0 || chIdx >= chapters.Count || chIdx >= book.ChapterUrls.Count)
                continue;

            var chRef = book.ChapterUrls[chIdx];
            string statusMsg = $"Retranslating ch {chNum} ({f + 1}/{totalToRetry})...";
            log?.Invoke(statusMsg);
            progress?.Report(new ProgressEventArgs { Current = f + 1, Total = totalToRetry, Message = statusMsg });

            string existingText = chapters[chIdx].Text;
            const string Notice = "[Translation unavailable — original text below]\n";
            string rawChinese = existingText.StartsWith(Notice)
                ? existingText[Notice.Length..]
                : existingText;

            if (string.IsNullOrWhiteSpace(rawChinese))
            {
                try
                {
                    string html = await _fetcher.Fetch(chRef.Url, log: log, ct: ct, forceBypass: book.Adapter.RequiresCfBypass);
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

            string chapterTitle = chRef.Title;
            string text = "";
            bool success = false;

            try
            {
                string translated = await _translator.Translate(combined, log, ct);
                int sepIdx = translated.IndexOf("===TITLE===", StringComparison.OrdinalIgnoreCase);
                if (sepIdx >= 0)
                {
                    chapterTitle = translated[..sepIdx].Trim();
                    text = translated[(sepIdx + "===TITLE===".Length)..].Trim();
                }
                else
                {
                    chapterTitle = chRef.Title;
                    text = translated;
                }

                if (!Translator.IsUntranslatedChinese(combined, text))
                {
                    success = true;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                log?.Invoke($"[retranslate ch {chNum} failed: {ex.Message}]");
            }

            if (success)
            {
                chapters[chIdx] = (chNum, chapterTitle, text);
                resolved++;
                if (checkpointPath != null)
                {
                    await CheckpointService.SaveChapterAsync(
                        checkpointPath, book.IndexUrl, chIdx, chapterTitle, text, writeLock);
                }
            }
            else
            {
                stillFailed.Add(chNum);
            }
        }

        LastTranslationFailures.Clear();
        LastTranslationFailures.AddRange(stillFailed);

        if (resolved > 0 && !string.IsNullOrWhiteSpace(outputPath))
        {
            log?.Invoke("Rebuilding EPUB with retranslated chapters...");
            if (File.Exists(outputPath)) File.Delete(outputPath);
            EpubBuilder.Build(outputPath, book.Title, book.TitleEn!, book.Author, book.AuthorEn!,
                chapters, LastCoverBytes, LastCoverMime ?? "image/jpeg", true, LastLockedFromChapter);

            if (LastTranslationFailures.Count == 0 && checkpointPath != null)
            {
                CheckpointService.Delete(checkpointPath);
            }
        }

        return (resolved, stillFailed.Count);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Sequential fetch + translate pipeline.
    /// Fetches and translates one chapter at a time — simple, reliable, no deadlocks.
    /// Progress is reported after each chapter completes.
    /// </summary>
    private async Task<(List<(int Idx, string Title, string Text)> chapters, int? lockedFromChapter)> DownloadChapters(
        BookInfo book, IProgress<ProgressEventArgs>? progress, Action<string>? log,
        CancellationToken ct = default, string? checkpointPath = null, bool translate = true)
    {
        var chapterList = book.ChapterUrls.Take(book.Total).ToList();
        int total = chapterList.Count;
        var results = new List<(int Idx, string Title, string Text)>(total);
        int? lockedFromChapter = null;
        LastTranslationFailures.Clear();

        // Load checkpoint — skip already-completed chapters
        var saved = checkpointPath != null
            ? await CheckpointService.LoadAsync(checkpointPath, total)
            : new (string title, string text)?[total];

        int alreadyDone = saved.Count(r => r != null);
        if (alreadyDone > 0)
            log?.Invoke($"Resuming from chapter {alreadyDone + 1} of {total} ({alreadyDone} already done)...");

        // Pre-seed progress so the UI shows the correct starting % immediately on resume
        if (alreadyDone > 0)
            progress?.Report(new ProgressEventArgs
            {
                Current = alreadyDone,
                Total   = total,
                Message = translate
                    ? $"Resuming: {alreadyDone} of {total} chapters already translated..."
                    : $"Resuming: {alreadyDone} of {total} chapters already downloaded..."
            });

        // Semaphore to serialize checkpoint writes
        var writeLock = new SemaphoreSlim(1, 1);

        for (int i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();

            // Skip chapters already in the checkpoint
            if (saved[i] != null)
            {
                string savedTitle = saved[i]!.Value.title;
                string savedText  = saved[i]!.Value.text;

                if (savedText.StartsWith("[Translation unavailable", StringComparison.OrdinalIgnoreCase) ||
                    (translate && Translator.IsUntranslatedChinese(savedTitle, savedText)))
                {
                    LastTranslationFailures.Add(i + 1);
                }

                // If adapter signals stop-on-first-locked and this checkpoint entry is empty, stop now
                if (book.Adapter.StopOnFirstLockedChapter && string.IsNullOrWhiteSpace(savedText))
                {
                    string statusMsg = $"Chapter {i + 1} is locked in {book.Adapter.SiteName}. Finished download at chapter {i}.";
                    log?.Invoke(statusMsg);
                    progress?.Report(new ProgressEventArgs { Current = i, Total = total, Message = statusMsg });
                    lockedFromChapter = i + 1;
                    break;
                }

                results.Add((i + 1, savedTitle, savedText));
                progress?.Report(new ProgressEventArgs
                {
                    Current = i + 1,
                    Total = total,
                    Message = translate ? $"Translated chapter {i + 1} of {total}..." : $"Downloaded chapter {i + 1} of {total}..."
                });
                continue;
            }

            var ch = chapterList[i];

            // Fetch with per-chapter retry
            string html = "";
            for (int fetchAttempt = 1; fetchAttempt <= 5; fetchAttempt++)
            {
                try
                {
                    html = await _fetcher.Fetch(ch.Url, log: log, ct: ct, forceBypass: book.Adapter.RequiresCfBypass);
                    break;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    if (fetchAttempt == 5)
                    {
                        log?.Invoke($"[fetch failed ch{i + 1} after 5 attempts] {ex.Message}");
                        html = "";
                    }
                    else
                    {
                        int delaySec = Math.Min(fetchAttempt * 2, 10);
                        log?.Invoke($"[fetch retry {fetchAttempt}/5 ch{i + 1}] {ex.Message} — waiting {delaySec}s");
                        await Task.Delay(delaySec * 1000, ct);
                    }
                }
            }

            var paras = book.Adapter.ExtractChapterText(html);

            // If this chapter is empty and the adapter says stop on first locked, end download now
            if (paras.Count == 0 && book.Adapter.StopOnFirstLockedChapter)
            {
                string statusMsg = $"Chapter {i + 1} is locked in {book.Adapter.SiteName}. Finished download at chapter {i}.";
                log?.Invoke(statusMsg);
                progress?.Report(new ProgressEventArgs { Current = i, Total = total, Message = statusMsg });
                lockedFromChapter = i + 1;
                break;
            }

            string chapterTitle = ch.Title;
            string text = "";
            bool translationFailed = false;

            if (paras.Count > 0)
            {
                if (translate)
                {
                    // Translate title and body together so we only need one translation call
                    // when both succeed. Title is prepended with a sentinel separator so we
                    // can split the result back out without ambiguity.
                    const string TitleSep = "\n===TITLE===\n";
                    string combined = ch.Title + TitleSep + string.Join("\n", paras);

                    int maxAttempts = Translator.IsGoogleRateLimited ? 2 : 5;
                    for (int transAttempt = 1; transAttempt <= maxAttempts; transAttempt++)
                    {
                        try
                        {
                            string translated = await _translator.Translate(combined, log, ct);

                            // Split translated result back into title + body
                            int sepIdx = translated.IndexOf("===TITLE===", StringComparison.OrdinalIgnoreCase);
                            if (sepIdx >= 0)
                            {
                                chapterTitle = translated[..sepIdx].Trim();
                                text         = translated[(sepIdx + "===TITLE===".Length)..].Trim();
                            }
                            else
                            {
                                // Separator was lost in translation — keep original title, use full result as body
                                chapterTitle = ch.Title;
                                text         = translated;
                            }

                            // Verify that the translated body is not still untranslated Chinese
                            if (Translator.IsUntranslatedChinese(combined, text))
                            {
                                throw new TranslationFailedException("Translation result still contained untranslated Chinese.");
                            }

                            translationFailed = false;
                            break;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            if (transAttempt == maxAttempts)
                            {
                                log?.Invoke($"[translate failed ch{i + 1} after {maxAttempts} attempts] {ex.Message} — keeping original");
                                chapterTitle      = ch.Title;
                                text              = string.Join("\n", paras);
                                translationFailed = true;
                                LastTranslationFailures.Add(i + 1);
                            }
                            else
                            {
                                int delaySec = Math.Min((int)Math.Pow(2, transAttempt), 15);
                                log?.Invoke($"[translate retry {transAttempt}/{maxAttempts} ch{i + 1}] waiting {delaySec}s...");
                                await Task.Delay(delaySec * 1000, ct);
                            }
                        }
                    }

                    // Prepend a visible notice when translation failed so readers know
                    // the chapter body is in the original language
                    if (translationFailed)
                        text = "[Translation unavailable — original text below]\n" + text;
                }
                else
                {
                    text = string.Join("\n", paras);
                }
            }

            results.Add((i + 1, chapterTitle, text));

            // Save to checkpoint so this chapter isn't re-downloaded on retry
            if (checkpointPath != null)
                await CheckpointService.SaveChapterAsync(
                    checkpointPath, book.IndexUrl, i, chapterTitle, text, writeLock);

            progress?.Report(new ProgressEventArgs
            {
                Current = i + 1,
                Total = total,
                Message = translate ? $"Translated chapter {i + 1} of {total}..." : $"Downloaded chapter {i + 1} of {total}..."
            });

            // ── Long download pacing (500+ chapters rate-limit prevention) ────────
            if (translate && (i + 1) < total)
            {
                if ((i + 1) % 25 == 0)
                {
                    int pauseSec = Translator.IsGoogleRateLimited ? 4 : 2;
                    log?.Invoke($"[Pacing pause: resting {pauseSec}s after chapter {i + 1} to prevent rate limits]");
                    await Task.Delay(pauseSec * 1000, ct);
                }
                else
                {
                    int chDelayMs = Translator.IsGoogleRateLimited ? 800 : 250;
                    await Task.Delay(chDelayMs, ct);
                }
            }
        }

        return (results, lockedFromChapter);
    }

    private async Task<(byte[]? bytes, string mime)> DownloadCover(
        string? coverUrl, Action<string>? log, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(coverUrl)) return (null, "image/jpeg");
        log?.Invoke("Downloading cover...");
        try
        {
            // 30-second hard cap per attempt so a stalled CDN (e.g. jjwxc.net) can't
            // freeze the entire download pipeline. Also linked to the user's ct so
            // pausing/cancelling during the cover fetch works immediately.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            // Strip Next.js image optimization parameters to get original quality
            string cleanUrl = NoveldexAdapter.NormalizeCoverUrl(coverUrl) ?? coverUrl;

            using var req = new HttpRequestMessage(HttpMethod.Get, cleanUrl);
            // Add a Referer derived from the cover URL's host so CDNs that check it
            // (e.g. jjwxc.net static servers) don't block or stall the request.
            // media.noveldex.io requires Referer: https://noveldex.io/
            try
            {
                string referer = NoveldexAdapter.GetCoverReferer(cleanUrl)
                    ?? $"{new Uri(cleanUrl).Scheme}://{new Uri(cleanUrl).Host}/";
                req.Headers.Add("Referer", referer);
            }
            catch { /* malformed URL — skip Referer */ }

            using var resp = await _gtClient.SendAsync(req, cts.Token);
            resp.EnsureSuccessStatusCode();
            byte[] bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token);

            string ext = Path.GetExtension(new Uri(coverUrl).AbsolutePath).ToLowerInvariant();
            string mime = ext switch { ".png" => "image/png", ".gif" => "image/gif", ".webp" => "image/webp", ".avif" => "image/avif", _ => "image/jpeg" };
            if (bytes.Length >= 4)
            {
                if (bytes[0] == 0x89 && bytes[1] == 0x50) mime = "image/png";
                else if (bytes[0] == 0xFF && bytes[1] == 0xD8) mime = "image/jpeg";
                else if (bytes[0] == 0x47 && bytes[1] == 0x49) mime = "image/gif";
                else if (bytes.Length >= 12 && bytes[4] == 0x66 && bytes[5] == 0x74 && bytes[6] == 0x79 && bytes[7] == 0x70) mime = "image/avif"; // ftyp
            }
            log?.Invoke($"Cover OK ({bytes.Length / 1024}KB, {mime})");
            return (bytes, mime);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // user cancelled — propagate
        }
        catch (Exception ex)
        {
            log?.Invoke($"Cover failed: {ex.Message} (using generated cover)");
            return (null, "image/jpeg");
        }
    }

    private static ISiteAdapter DetectAdapter(string url) =>
        Adapters.FirstOrDefault(a => a.Matches(url))
        ?? throw new Exception($"No supported adapter for URL: {url}\nSupported: 52shuku.net, czbooks.net, dmxs.org, 69shuba.com, quanben.io, situu.cc, yamibo.com, zhenhunxiaoshuo.com, noveldex.io, shubaow.net");

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

    /// <summary>
    /// Cover extractor for hint pages (e.g. quanben.io non-AMP book info page).
    /// Looks for the CDN thumbnail img tag first, then falls back to any image URL.
    /// </summary>
    private static string? TryExtractCoverFromHint(string html)
    {
        // quanben.io CDN: img.c0m.io/quanben.io/upload/thumbnail/...
        var cdnM = Regex.Match(html,
            @"src=[""'](https?://img\.c0m\.io/[^""']+)[""']",
            RegexOptions.IgnoreCase);
        if (cdnM.Success) return cdnM.Groups[1].Value.Trim();

        // Any absolute img src that looks like an image file
        var imgM = Regex.Match(html,
            @"<img[^>]+src=[""'](https?://[^""']+\.(?:jpg|jpeg|png|webp|gif))[""']",
            RegexOptions.IgnoreCase);
        if (imgM.Success) return imgM.Groups[1].Value.Trim();

        return null;
    }
}
