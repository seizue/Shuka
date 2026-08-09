using System.Text;
using Shuka;
using Shuka.Core;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
Console.OutputEncoding = Encoding.UTF8;



// Parse translation flags
bool translate = true;
var argsList = args.ToList();
if (argsList.RemoveAll(arg => arg.Equals("--no-translate", StringComparison.OrdinalIgnoreCase) ||
                             arg.Equals("--original", StringComparison.OrdinalIgnoreCase)) > 0)
{
    translate = false;
}
args = argsList.ToArray();

// Background release check (non-blocking).
ReleaseUpdateService.StartBackgroundCheck(message =>
{
    try
    {
        Console.WriteLine();
        Console.WriteLine(message);
        Console.WriteLine("Open releases: https://github.com/seizue/Shuka/releases");
        Console.WriteLine();
    }
    catch
    {
        // Keep CLI flow resilient if console output fails.
    }
});

// ── Usage ─────────────────────────────────────────────────────────────────────
if (args.Length == 0)
{
    // No arguments — launch the interactive TUI
    var siteHandlerTui = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
    using var siteClientTui = new HttpClient(siteHandlerTui) { Timeout = TimeSpan.FromSeconds(30) };
    siteClientTui.DefaultRequestHeaders.Add("User-Agent",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
    siteClientTui.DefaultRequestHeaders.Add("Accept-Language", "zh-TW,zh;q=0.9,zh-CN;q=0.8");
    siteClientTui.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

    var httpHandlerTui = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
    using var httpClientTui = new HttpClient(httpHandlerTui) { Timeout = TimeSpan.FromSeconds(45) };
    httpClientTui.DefaultRequestHeaders.Add("User-Agent",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");

    await using var fetcherTui    = new PlaywrightFetcher(siteClientTui);
    var             translatorTui = new Translator(httpClientTui);
    var             downloaderTui = new Downloader(fetcherTui, translatorTui, httpClientTui);

    await Tui.RunAsync(downloaderTui, translate);
    return;
}

// ── Playwright browser install passthrough (used by installer) ────────────────
if (args.Length >= 2 && args[0] == "playwright" && args[1] == "install")
{
    Environment.Exit(Microsoft.Playwright.Program.Main(args.Skip(1).ToArray()));
    return;
}

// ── --solve-cf: manual CF challenge solver ────────────────────────────────────
if (args.Length >= 2 && args[0] == "--solve-cf")
{
    await PlaywrightFetcher.SolveCfInteractiveAsync(args[1]);
    return;
}

// ── --solve-noveldex: open browser so user can load a noveldex chapter ────────
if (args.Length >= 2 && args[0] == "--solve-noveldex")
{
    Console.WriteLine("Open the chapter page in the browser, let it fully load, then press Enter.");
    await PlaywrightFetcher.SolveNoveldexInteractiveAsync(args[1]);
    return;
}

// ── --dump-html: debug — fetch a URL with Playwright and dump HTML to file ────
if (args.Length >= 2 && args[0] == "--dump-html")
{
    var sh = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
    using var sc = new HttpClient(sh);
    sc.DefaultRequestHeaders.Add("User-Agent",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
    await using var dbgFetcher = new PlaywrightFetcher(sc);
    Console.WriteLine($"Fetching: {args[1]}");
    string dumpHtml = await dbgFetcher.FetchAsync(args[1]);
    string dumpPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "shuka-dump.html");
    File.WriteAllText(dumpPath, dumpHtml, Encoding.UTF8);
    Console.WriteLine($"Dumped {dumpHtml.Length} bytes → {dumpPath}");
    return;
}

// ── HTTP clients ──────────────────────────────────────────────────────────────
var siteHandler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
using var siteClient = new HttpClient(siteHandler) { Timeout = TimeSpan.FromSeconds(30) };
siteClient.DefaultRequestHeaders.Add("User-Agent",
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
siteClient.DefaultRequestHeaders.Add("Accept-Language", "zh-TW,zh;q=0.9,zh-CN;q=0.8");
siteClient.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

var httpHandler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
using var httpClient = new HttpClient(httpHandler) { Timeout = TimeSpan.FromSeconds(45) };
httpClient.DefaultRequestHeaders.Add("User-Agent",
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");

await using var fetcher    = new PlaywrightFetcher(siteClient);
var             translator = new Translator(httpClient);
var             downloader = new Downloader(fetcher, translator, httpClient);

// ── --batch mode ──────────────────────────────────────────────────────────────
if (args[0].Equals("--batch", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 2) { Console.WriteLine("Error: --batch requires a file path."); return; }
    if (!File.Exists(args[1])) { Console.WriteLine($"Error: file not found: {args[1]}"); return; }

    var urls = File.ReadAllLines(args[1])
        .Select(l => l.Trim())
        .Where(l => l.Length > 0 && !l.StartsWith('#'))
        .ToList();

    if (urls.Count == 0) { Console.WriteLine("No URLs found in batch file."); return; }
    Console.WriteLine($"Batch mode: {urls.Count} book(s) found.\n");

    Console.WriteLine("=== Phase 1: Gathering book info ===");
    var books = new List<BookInfo>();
    foreach (var url in urls)
    {
        try   { books.Add(await downloader.GatherBookInfoAsync(url)); }
        catch (Exception ex) { Console.WriteLine($"  [skip] {url} — {ex.Message}"); }
    }

    Console.WriteLine("\n=== Books to download ===");
    for (int i = 0; i < books.Count; i++)
    {
        var b = books[i];
        Console.WriteLine($"  [{i + 1}] {b.Title} by {b.Author} — {b.Total} chapters" +
                          $" — cover: {(b.CoverUrl != null ? "found" : "none")}");
    }
    Console.WriteLine();

    Console.WriteLine("=== Phase 2: Downloading & building EPUBs ===");
    for (int i = 0; i < books.Count; i++)
    {
        Console.WriteLine($"\n[{i + 1}/{books.Count}]");
        try   { await downloader.ProcessBookAsync(books[i], null, translate); }
        catch (Exception ex) { Console.WriteLine($"  [error] {books[i].Title}: {ex.Message}"); }
    }
    Console.WriteLine("\nBatch complete.");
    return;
}

// ── Single book mode ──────────────────────────────────────────────────────────
{
    string  indexUrl  = args[0];
    string? outFile   = args.Length > 2 && !string.IsNullOrWhiteSpace(args[2]) ? args[2] : null;
    string? coverUrl  = args.Length > 3 && !string.IsNullOrWhiteSpace(args[3]) ? args[3] : null;

    // Parse chapter arg: "200" = first 200, "100-200" = chapters 100 to 200
    int chapterLimit = 0;
    int chapterFrom  = 0;
    if (args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]))
    {
        string chapArg = args[1];
        if (chapArg.Contains('-'))
        {
            var parts = chapArg.Split('-');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out int from) &&
                int.TryParse(parts[1], out int to) &&
                from >= 1 && to >= from)
            {
                chapterFrom  = from;
                chapterLimit = to - from + 1;
            }
            else
            {
                Console.WriteLine("Invalid range format. Use: 100-200");
                return;
            }
        }
        else
        {
            chapterLimit = int.TryParse(chapArg, out int n) ? n : 0;
        }
    }

    Console.WriteLine("=== Phase 1: Gathering book info ===");
    var book = await downloader.GatherBookInfoAsync(indexUrl, chapterLimit, coverUrl, chapterFrom);

    Console.WriteLine($"  Title:    {book.Title}");
    Console.WriteLine($"  Author:   {book.Author}");
    Console.WriteLine($"  Chapters: {book.Total} (of {book.ChapterUrls.Count} found)" +
                      (chapterFrom > 0 ? $" starting from ch{chapterFrom}" : ""));
    Console.WriteLine($"  Cover:    {book.CoverUrl ?? "none (will generate)"}");
    Console.WriteLine();

    if (book.Total == 0) { Console.WriteLine("No chapters found."); return; }

    Console.WriteLine("=== Phase 2: Downloading & building EPUB ===");
    await downloader.ProcessBookAsync(book, outFile, translate);
}
