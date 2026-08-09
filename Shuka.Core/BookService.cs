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

    public static readonly ISiteAdapter[] Adapters =
        [new ShukuAdapter(), new CzBooksAdapter(), new DmxsAdapter(), new ShubaAdapter(), new QuanbenAdapter(), new SituuAdapter(), new YamiboAdapter(), new ZhenhunAdapter(), new NoveldexAdapter()];

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

            await Task.WhenAll(titleTask, authorTask, coverTask);

            book.TitleEn = titleTask.Result;
            book.AuthorEn = authorTask.Result;
            (coverBytes, coverMime) = coverTask.Result;

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
        var chapters = await DownloadChapters(book, progress, log, ct, checkpointPath, translate);

        ct.ThrowIfCancellationRequested();
        log?.Invoke("Building EPUB...");
        if (File.Exists(outputPath)) File.Delete(outputPath);
        EpubBuilder.Build(outputPath, book.Title, book.TitleEn!, book.Author, book.AuthorEn!,
            chapters, coverBytes, coverMime, translate);

        // Delete checkpoint on success — no longer needed
        if (checkpointPath != null) CheckpointService.Delete(checkpointPath);

        return outputPath;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Sequential fetch + translate pipeline.
    /// Fetches and translates one chapter at a time — simple, reliable, no deadlocks.
    /// Progress is reported after each chapter completes.
    /// </summary>
    private async Task<List<(int Idx, string Title, string Text)>> DownloadChapters(
        BookInfo book, IProgress<ProgressEventArgs>? progress, Action<string>? log,
        CancellationToken ct = default, string? checkpointPath = null, bool translate = true)
    {
        var chapterList = book.ChapterUrls.Take(book.Total).ToList();
        int total = chapterList.Count;
        var results = new List<(int Idx, string Title, string Text)>(total);

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
                results.Add((i + 1, saved[i]!.Value.title, saved[i]!.Value.text));
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

            // Translate with per-chapter retry — more attempts + longer backoff
            // for large novels where Google rate-limits more aggressively
            var paras = book.Adapter.ExtractChapterText(html);
            string text = "";
            if (paras.Count > 0)
            {
                if (translate)
                {
                    for (int transAttempt = 1; transAttempt <= 6; transAttempt++)
                    {
                        try
                        {
                            text = await _translator.Translate(string.Join("\n", paras), log, ct);
                            break;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            if (transAttempt == 6)
                            {
                                log?.Invoke($"[translate failed ch{i + 1} after 6 attempts] {ex.Message} — keeping original");
                                text = string.Join("\n", paras);
                            }
                            else
                            {
                                // Exponential backoff: 2s, 4s, 8s, 16s, 30s max
                                int delaySec = Math.Min((int)Math.Pow(2, transAttempt), 30);
                                log?.Invoke($"[translate retry {transAttempt}/6 ch{i + 1}] waiting {delaySec}s...");
                                await Task.Delay(delaySec * 1000, ct);
                            }
                        }
                    }
                }
                else
                {
                    text = string.Join("\n", paras);
                }
            }

            results.Add((i + 1, ch.Title, text));

            // Save to checkpoint so this chapter isn't re-downloaded on retry
            if (checkpointPath != null)
                await CheckpointService.SaveChapterAsync(
                    checkpointPath, book.IndexUrl, i, ch.Title, text, writeLock);

            progress?.Report(new ProgressEventArgs
            {
                Current = i + 1,
                Total = total,
                Message = translate ? $"Translated chapter {i + 1} of {total}..." : $"Downloaded chapter {i + 1} of {total}..."
            });
        }

        return results;
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
            string cleanUrl = coverUrl;
            if (coverUrl.Contains("/_next/image?url="))
            {
                var match = Regex.Match(coverUrl, @"url=([^&]+)");
                if (match.Success)
                {
                    string decoded = Uri.UnescapeDataString(match.Groups[1].Value);
                    if (decoded.Contains('?'))
                        decoded = decoded.Substring(0, decoded.IndexOf('?'));
                    cleanUrl = decoded.StartsWith("http") ? decoded : coverUrl;
                }
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, cleanUrl);
            // Add a Referer derived from the cover URL's host so CDNs that check it
            // (e.g. jjwxc.net static servers) don't block or stall the request.
            try
            {
                var uri = new Uri(coverUrl);
                req.Headers.Add("Referer", $"{uri.Scheme}://{uri.Host}/");
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
        ?? throw new Exception($"No supported adapter for URL: {url}\nSupported: 52shuku.net, czbooks.net, dmxs.org, 69shuba.com, quanben.io, situu.cc, yamibo.com, zhenhunxiaoshuo.com, noveldex.io");

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
