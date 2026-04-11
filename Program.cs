using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

// Usage
if (args.Length == 0)
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  Normal:  ShukuEpub <index-url> [pages] [output.epub] [cover-url]");
    Console.WriteLine("  Batch:   ShukuEpub --batch <urls-file.txt>");
    Console.WriteLine();
    Console.WriteLine("  pages = how many pages to download (0 = all)");
    Console.WriteLine("  urls-file = text file with one URL per line (pages=0 for each)");
    Console.WriteLine();
    Console.WriteLine("  e.g. ShukuEpub https://www.52shuku.net/bl/09_b/bkd7d.html 3");
    Console.WriteLine("  e.g. ShukuEpub --batch mylist.txt");
    return;
}


// HTTP client for the site (GBK-aware, with site headers)
var sh = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
using var site = new HttpClient(sh) { Timeout = TimeSpan.FromSeconds(30) };
site.DefaultRequestHeaders.Add("User-Agent",
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
site.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9");
site.DefaultRequestHeaders.Add("Referer", "https://www.52shuku.net/");

// HTTP client for Google Translate and cover downloads (clean, no site headers)
var gh = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
using var gt = new HttpClient(gh) { Timeout = TimeSpan.FromSeconds(20) };
gt.DefaultRequestHeaders.Add("User-Agent",
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");


// Fetch page as properly decoded string (detects GBK/UTF-8 from meta charset)
async Task<string> Fetch(string url, int retries = 4)
{
    int delay = 1000;
    Exception? last = null;
    for (int i = 0; i <= retries; i++)
    {
        try
        {
            byte[] bytes = await site.GetByteArrayAsync(url);
            string ascii = Encoding.ASCII.GetString(bytes);
            var cm = Regex.Match(ascii, @"charset\s*=\s*[""']?\s*([\w-]+)", RegexOptions.IgnoreCase);
            string charset = cm.Success ? cm.Groups[1].Value.Trim() : "gbk";
            Encoding enc;
            try   { enc = Encoding.GetEncoding(charset); }
            catch { enc = Encoding.GetEncoding("gbk"); }
            return enc.GetString(bytes);
        }
        catch (Exception ex) { last = ex; await Task.Delay(delay); delay = Math.Min(delay * 2, 16000); }
    }
    throw new Exception($"Fetch failed: {url} — {last?.Message}");
}


// Extract Chinese paragraphs using pure regex (no HtmlAgilityPack)
static List<string> ExtractParagraphs(string html)
{
    html = Regex.Replace(html, @"<script[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
    html = Regex.Replace(html, @"<style[\s\S]*?</style>",   "", RegexOptions.IgnoreCase);
    var result = new List<string>();
    foreach (Match m in Regex.Matches(html, @"<p(?:\s[^>]*)?>([^<]*(?:<(?!/p>)[^<]*)*)</p>", RegexOptions.IgnoreCase))
    {
        string inner = m.Groups[1].Value;
        inner = Regex.Replace(inner, @"<[^>]+>", "");
        inner = inner.Replace("&nbsp;", " ").Replace("&amp;", "&")
                     .Replace("&lt;", "<").Replace("&gt;", ">")
                     .Replace("&quot;", "\"").Replace("&#39;", "'")
                     .Replace("\u3000", " ").Trim();
        if (inner.Length > 0 && Regex.IsMatch(inner, @"[\u4e00-\u9fff]"))
            result.Add(inner);
    }
    return result;
}


// Translate a single chunk via Google Translate (unofficial API)
async Task<string> TranslateChunk(string chunk, int retries = 4)
{
    int delay = 1500;
    for (int attempt = 0; attempt <= retries; attempt++)
    {
        try
        {
            string url = "https://translate.googleapis.com/translate_a/single" +
                $"?client=gtx&sl=zh-CN&tl=en&dt=t&q={Uri.EscapeDataString(chunk)}";
            string json = await gt.GetStringAsync(url);
            using var jdoc = JsonDocument.Parse(json);
            var sb = new StringBuilder();
            foreach (var seg in jdoc.RootElement[0].EnumerateArray())
                if (seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0
                    && seg[0].ValueKind == JsonValueKind.String)
                    sb.Append(seg[0].GetString());
            string r = sb.ToString().Trim();
            if (!string.IsNullOrEmpty(r)) return r;
        }
        catch (Exception ex) when (attempt < retries)
        {
            Console.WriteLine($"\n  [translate retry {attempt+1}] {ex.Message}");
            await Task.Delay(delay);
            delay = Math.Min(delay * 2, 10000);
        }
        catch (Exception ex) { Console.WriteLine($"\n  [translate failed] {ex.Message}"); break; }
    }
    return chunk; // fallback: return original
}


// Split text into chunks and translate in parallel
async Task<string> Translate(string text)
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

    // Translate all chunks in parallel (max 4 concurrent to avoid rate-limiting)
    var sem = new SemaphoreSlim(4);
    var tasks = chunks.Select(async (chunk, i) =>
    {
        await sem.WaitAsync();
        try   { return (i, text: await TranslateChunk(chunk)); }
        finally { sem.Release(); }
    }).ToList();

    var results = await Task.WhenAll(tasks);
    return string.Join("\n", results.OrderBy(r => r.i).Select(r => r.text));
}


// Try to extract a cover image URL from the index page HTML
static string? TryExtractCover(string html, string indexUrl)
{
    // Look for og:image or common cover img patterns
    var og = Regex.Match(html, @"<meta[^>]+property=[""']og:image[""'][^>]+content=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
    if (!og.Success)
        og = Regex.Match(html, @"<meta[^>]+content=[""']([^""']+)[""'][^>]+property=[""']og:image[""']", RegexOptions.IgnoreCase);
    if (og.Success) return og.Groups[1].Value.Trim();

    // Fallback: first img with "cover" in src or alt
    var img = Regex.Match(html, @"<img[^>]+src=[""']([^""']+cover[^""']*)[""']", RegexOptions.IgnoreCase);
    if (!img.Success)
        img = Regex.Match(html, @"<img[^>]+alt=[""'][^""']*cover[^""']*[""'][^>]+src=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
    if (img.Success)
    {
        string src = img.Groups[1].Value.Trim();
        return src.StartsWith("http") ? src : new Uri(new Uri(indexUrl), src).ToString();
    }
    return null;
}


// Gather title, author, page list and cover URL from the index page (no chapter downloading yet)
async Task<BookInfo> GatherBookInfo(string indexUrl, int pageLimit = 0, string? forceCoverUrl = null)
{
    // If user passed a chapter page (e.g. bjY59_2.html), strip back to the index (bjY59.html)
    indexUrl = Regex.Replace(indexUrl, @"_\d+\.html$", ".html");

    Console.WriteLine($"  Gathering: {indexUrl}");
    string indexHtml = await Fetch(indexUrl);

    string title = Regex.Match(indexHtml, @"<h1[^>]*>([\s\S]*?)</h1>", RegexOptions.IgnoreCase).Groups[1].Value;
    title = Regex.Replace(title, @"<[^>]+>", "").Trim();
    title = Regex.Replace(title, @"\s*\(\d+\)\s*$", "").Trim();
    if (string.IsNullOrWhiteSpace(title))
        title = Regex.Match(indexUrl, @"/([^/]+)\.html$").Groups[1].Value;

    string author = "Unknown";
    var am = Regex.Match(indexHtml, @"作者[：:]\s*([^\s【\n】<&]+)");
    if (am.Success) author = am.Groups[1].Value.Trim();

    string baseUrl = Regex.Replace(indexUrl, @"\.html$", "");
    var pageUrls = Regex.Matches(indexHtml, @"href=[""']?([^""'\s>]+)[""']?", RegexOptions.IgnoreCase)
        .Select(m => m.Groups[1].Value)
        .Select(h => h.StartsWith("http") ? h : new Uri(new Uri(indexUrl), h).ToString())
        .Where(u => u.StartsWith(baseUrl + "_") && u.EndsWith(".html"))
        .Distinct()
        .OrderBy(u => { var m = Regex.Match(u, @"_(\d+)\.html$"); return m.Success ? int.Parse(m.Groups[1].Value) : 0; })
        .ToList();

    int total = pageLimit > 0 ? Math.Min(pageLimit, pageUrls.Count) : pageUrls.Count;

    string? coverUrl = forceCoverUrl ?? TryExtractCover(indexHtml, indexUrl);

    return new BookInfo(indexUrl, title, author, pageUrls, total, coverUrl);
}


// Download cover image bytes, detect mime type from magic bytes
async Task<(byte[]? bytes, string mime)> DownloadCover(string? coverUrl)
{
    if (string.IsNullOrWhiteSpace(coverUrl)) return (null, "image/jpeg");
    Console.Write($"  Downloading cover...");
    try
    {
        byte[] bytes = await gt.GetByteArrayAsync(coverUrl);
        string ext = Path.GetExtension(new Uri(coverUrl).AbsolutePath).ToLowerInvariant();
        string mime = ext switch { ".png" => "image/png", ".gif" => "image/gif", ".webp" => "image/webp", _ => "image/jpeg" };
        if (bytes.Length >= 4)
        {
            if (bytes[0] == 0x89 && bytes[1] == 0x50) mime = "image/png";
            else if (bytes[0] == 0xFF && bytes[1] == 0xD8) mime = "image/jpeg";
            else if (bytes[0] == 0x47 && bytes[1] == 0x49) mime = "image/gif";
        }
        Console.WriteLine($" OK ({bytes.Length / 1024}KB, {mime})");
        return (bytes, mime);
    }
    catch (Exception ex)
    {
        Console.WriteLine($" Failed: {ex.Message} (using generated cover)");
        return (null, "image/jpeg");
    }
}


// Download and translate all chapter pages for a book (fetch-ahead pipeline)
async Task<List<(int Idx, string Text)>> DownloadChapters(BookInfo book)
{
    // Semaphore limits concurrent site fetches to avoid hammering the server
    var fetchSem = new SemaphoreSlim(3);
    var t0 = DateTime.Now;
    int done = 0;

    // Kick off all fetches immediately (gated by semaphore)
    var fetchTasks = book.PageUrls.Take(book.Total).Select(async (url, i) =>
    {
        await fetchSem.WaitAsync();
        try   { return (i, html: await Fetch(url)); }
        finally { fetchSem.Release(); }
    }).ToArray();

    // As each fetch completes (in order), translate and collect
    var chapters = new List<(int Idx, string Text)>(book.Total);
    bool firstDebug = true;

    for (int i = 0; i < book.Total; i++)
    {
        double elapsed = (DateTime.Now - t0).TotalSeconds;
        string eta = i > 0 ? $"~{TimeSpan.FromSeconds(elapsed / i * (book.Total - i)):mm\\:ss} left" : "";
        Console.Write($"\r  [{i+1}/{book.Total}] Translating... {eta}      ");

        var (_, html) = await fetchTasks[i];
        var paras = ExtractParagraphs(html);

        if (firstDebug && paras.Count > 0)
        {
            Console.WriteLine($"\n  [debug] first para ({paras[0].Length} chars): {paras[0][..Math.Min(60, paras[0].Length)]}");
            firstDebug = false;
        }

        string english = await Translate(string.Join("\n", paras));
        chapters.Add((i + 1, english));

        done++;
    }

    return chapters;
}


// Build the output .epub file path (defaults to Downloads folder)
static string BuildOutputPath(BookInfo book, string? outFile)
{
    if (outFile != null) return outFile;
    string safeName = Regex.Replace(book.TitleEn ?? book.Title, @"[\\/:*?""<>|]", "_").Trim('_');
    if (string.IsNullOrWhiteSpace(safeName))
        safeName = Regex.Match(book.IndexUrl, @"/([^/]+)\.html$").Groups[1].Value;
    string fileName = safeName[..Math.Min(safeName.Length, 60)] +
                      (book.PageLimit > 0 ? $"_pages1-{book.Total}" : "") + ".epub";
    string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    return Path.Combine(downloads, fileName);
}


// Translate metadata, download cover, download chapters, build EPUB
async Task ProcessBook(BookInfo book, string? outFile = null)
{
    Console.WriteLine($"\n--- {book.Title} ({book.Total} pages) ---");

    // Translate title/author
    Console.Write("  Translating title/author...");
    book.TitleEn  = await Translate(book.Title);
    book.AuthorEn = await Translate(book.Author);
    Console.WriteLine($" done");
    Console.WriteLine($"  Title (EN):  {book.TitleEn}");
    Console.WriteLine($"  Author (EN): {book.AuthorEn}");

    // Download cover
    var (coverBytes, coverMime) = await DownloadCover(book.CoverUrl);

    // Download chapters
    var chapters = await DownloadChapters(book);

    // Build EPUB
    Console.WriteLine("\n  Building EPUB...");
    string path = BuildOutputPath(book, outFile);
    if (File.Exists(path)) File.Delete(path);
    BuildEpub(path, book.Title, book.TitleEn, book.Author, book.AuthorEn, chapters, coverBytes, coverMime);
    Console.WriteLine($"  Saved: {Path.GetFullPath(path)}");
}


// Entry point
bool isBatch = args[0].Equals("--batch", StringComparison.OrdinalIgnoreCase);

if (isBatch)
{
    // Batch mode
    if (args.Length < 2) { Console.WriteLine("Error: --batch requires a file path."); return; }
    string batchFile = args[1];
    if (!File.Exists(batchFile)) { Console.WriteLine($"Error: file not found: {batchFile}"); return; }

    var urls = File.ReadAllLines(batchFile)
        .Select(l => l.Trim())
        .Where(l => l.Length > 0 && !l.StartsWith("#"))
        .ToList();

    if (urls.Count == 0) { Console.WriteLine("No URLs found in batch file."); return; }

    Console.WriteLine($"Batch mode: {urls.Count} book(s) found.\n");

    // Phase 1: Gather all book info
    Console.WriteLine("=== Phase 1: Gathering book info ===");
    var books = new List<BookInfo>();
    foreach (var url in urls)
    {
        try { books.Add(await GatherBookInfo(url)); }
        catch (Exception ex) { Console.WriteLine($"  [skip] {url} — {ex.Message}"); }
    }

    // Preview
    Console.WriteLine("\n=== Books to download ===");
    for (int i = 0; i < books.Count; i++)
    {
        var b = books[i];
        Console.WriteLine($"  [{i+1}] {b.Title} by {b.Author} — {b.Total} pages — cover: {(b.CoverUrl != null ? "found" : "none")}");
    }
    Console.WriteLine();

    // Phase 2: Download each
    Console.WriteLine("=== Phase 2: Downloading & building EPUBs ===");
    for (int i = 0; i < books.Count; i++)
    {
        Console.WriteLine($"\n[{i+1}/{books.Count}]");
        try { await ProcessBook(books[i]); }
        catch (Exception ex) { Console.WriteLine($"  [error] {books[i].Title}: {ex.Message}"); }
    }

    Console.WriteLine("\nBatch complete.");
}
else
{
    // Normal mode
    string indexUrl  = args[0];
    int    pageLimit = args.Length > 1 && int.TryParse(args[1], out int pl) ? pl : 0;
    string? outFile  = args.Length > 2 && !string.IsNullOrWhiteSpace(args[2]) ? args[2] : null;
    string? coverUrl = args.Length > 3 && !string.IsNullOrWhiteSpace(args[3]) ? args[3] : null;

    // Phase 1: Gather
    Console.WriteLine("=== Phase 1: Gathering book info ===");
    var book = await GatherBookInfo(indexUrl, pageLimit, coverUrl);

    Console.WriteLine($"  Title:  {book.Title}");
    Console.WriteLine($"  Author: {book.Author}");
    Console.WriteLine($"  Pages:  {book.Total} (of {book.PageUrls.Count} found)");
    Console.WriteLine($"  Cover:  {(book.CoverUrl != null ? book.CoverUrl : "none (will generate)")}");
    Console.WriteLine();

    if (book.Total == 0) { Console.WriteLine("No pages found."); return; }

    // Phase 2: Download
    Console.WriteLine("=== Phase 2: Downloading & building EPUB ===");
    await ProcessBook(book, outFile);
}


// Build EPUB zip from translated chapters
static void BuildEpub(string path, string titleZh, string titleEn, string authorZh, string authorEn,
    List<(int Idx, string Text)> chapters, byte[]? coverBytes, string coverMime)
{
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

    var mte = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
    using (var w = new StreamWriter(mte.Open())) w.Write("application/epub+zip");

    W(zip, "META-INF/container.xml",
        "<?xml version=\"1.0\"?>" +
        "<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">" +
        "<rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/>" +
        "</rootfiles></container>");

    string uid = $"52shuku-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
    var items = new List<(string Id, string Fname, string Label)>();

    string coverItemId   = "cover-image";
    string coverExt      = coverMime == "image/png" ? "png" : coverMime == "image/gif" ? "gif" : "jpg";
    string coverImgFname = $"cover.{coverExt}";
    string coverMimeAttr = coverMime;

    if (coverBytes != null)
    {
        var ce = zip.CreateEntry($"OEBPS/{coverImgFname}", CompressionLevel.NoCompression);
        using var cs = ce.Open();
        cs.Write(coverBytes, 0, coverBytes.Length);
    }
    else
    {
        coverImgFname = "cover.svg";
        coverMimeAttr = "image/svg+xml";
        W(zip, "OEBPS/cover.svg", GenerateCoverSvg(titleEn, titleZh, authorEn, authorZh));
    }

    W(zip, "OEBPS/cover.xhtml",
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
        "<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.1//EN\" \"http://www.w3.org/TR/xhtml11/DTD/xhtml11.dtd\">" +
        "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>Cover</title>" +
        "<style>body{margin:0;padding:0;text-align:center;background:#000;}" +
        "img{max-width:100%;max-height:100vh;}</style>" +
        "</head><body>" +
        $"<img src=\"{coverImgFname}\" alt=\"Cover\"/>" +
        "</body></html>");
    items.Add(("cover", "cover.xhtml", "Cover"));

    W(zip, "OEBPS/titlepage.xhtml",
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
        "<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.1//EN\" \"http://www.w3.org/TR/xhtml11/DTD/xhtml11.dtd\">" +
        "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>Title Page</title>" +
        "<style>" +
        "body{font-family:Georgia,serif;text-align:center;margin:3em 2em;}" +
        ".title-en{font-size:1.8em;font-weight:bold;margin-bottom:0.3em;}" +
        ".title-zh{font-size:1.3em;color:#555;margin-bottom:1.5em;}" +
        ".author-label{font-size:0.9em;color:#888;text-transform:uppercase;letter-spacing:0.1em;}" +
        ".author-en{font-size:1.2em;font-weight:bold;margin-top:0.2em;}" +
        ".author-zh{font-size:1em;color:#555;}" +
        ".divider{margin:2em auto;width:60px;border-top:2px solid #ccc;}" +
        ".source{font-size:0.8em;color:#aaa;margin-top:3em;}" +
        "</style></head><body>" +
        $"<div class=\"title-en\">{X(titleEn)}</div>" +
        $"<div class=\"title-zh\">{X(titleZh)}</div>" +
        "<div class=\"divider\"></div>" +
        "<div class=\"author-label\">Author</div>" +
        $"<div class=\"author-en\">{X(authorEn)}</div>" +
        $"<div class=\"author-zh\">{X(authorZh)}</div>" +
        "<div class=\"source\">Source: 52shuku.net — Translated to English</div>" +
        "</body></html>");
    items.Add(("titlepage", "titlepage.xhtml", "Title Page"));

    foreach (var (idx, text) in chapters)
    {
        string id = $"ch{idx}", fname = $"ch{idx}.xhtml";
        items.Add((id, fname, $"Page {idx}"));
        var paras = text.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l))
                        .Select(l => $"<p>{X(l.Trim())}</p>");
        W(zip, $"OEBPS/{fname}",
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.1//EN\" \"http://www.w3.org/TR/xhtml11/DTD/xhtml11.dtd\">" +
            "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head>" +
            $"<title>Page {idx}</title>" +
            "<style>body{font-family:Georgia,serif;line-height:1.8;margin:2em}p{margin:0.5em 0}</style>" +
            $"</head><body><h2>Page {idx}</h2>" +
            string.Join("", paras) + "</body></html>");
    }

    string coverItem = $"<item id=\"{coverItemId}\" href=\"{coverImgFname}\" media-type=\"{coverMimeAttr}\" properties=\"cover-image\"/>";
    string mf = coverItem + string.Join("", items.Select(c => $"<item id=\"{c.Id}\" href=\"{c.Fname}\" media-type=\"application/xhtml+xml\"/>"));
    string sp = string.Join("", items.Select(c => $"<itemref idref=\"{c.Id}\"/>"));
    string np = string.Join("", items.Select((c, i) =>
        $"<navPoint id=\"n{i+1}\" playOrder=\"{i+1}\"><navLabel><text>{X(c.Label)}</text></navLabel><content src=\"{c.Fname}\"/></navPoint>"));

    W(zip, "OEBPS/content.opf",
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
        "<package xmlns=\"http://www.idpf.org/2007/opf\" unique-identifier=\"uid\" version=\"2.0\">" +
        "<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">" +
        $"<dc:title>{X(titleEn)}</dc:title><dc:creator>{X(authorEn)}</dc:creator>" +
        $"<dc:language>en</dc:language><dc:identifier id=\"uid\">{uid}</dc:identifier>" +
        "<meta name=\"cover\" content=\"cover-image\"/>" +
        $"</metadata><manifest><item id=\"ncx\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/>{mf}</manifest>" +
        $"<spine toc=\"ncx\">{sp}</spine></package>");

    W(zip, "OEBPS/toc.ncx",
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
        "<!DOCTYPE ncx PUBLIC \"-//NISO//DTD ncx 2005-1//EN\" \"http://www.daisy.org/z3986/2005/ncx-2005-1.dtd\">" +
        "<ncx xmlns=\"http://www.daisy.org/z3986/2005/ncx/\" version=\"2005-1\">" +
        $"<head><meta name=\"dtb:uid\" content=\"{uid}\"/></head>" +
        $"<docTitle><text>{X(titleEn)}</text></docTitle><navMap>{np}</navMap></ncx>");
}


// Generate a styled SVG cover when no image URL is provided
static string GenerateCoverSvg(string titleEn, string titleZh, string authorEn, string authorZh)
{
    var palettes = new[]
    {
        ("#1a1a2e", "#e94560", "#ffffff"),
        ("#0f3460", "#e94560", "#ffffff"),
        ("#16213e", "#0f3460", "#e2b96f"),
        ("#2d1b69", "#11998e", "#ffffff"),
        ("#1a1a1a", "#c0392b", "#f5f5f5"),
        ("#0d2137", "#f7971e", "#ffffff"),
    };
    int pick = Math.Abs(titleEn.GetHashCode()) % palettes.Length;
    var (bg, accent, fg) = palettes[pick];

    var titleLines = WrapText(titleEn, 22);
    var titleSvg = string.Join("", titleLines.Select((l, i) =>
        $"<tspan x=\"300\" dy=\"{(i == 0 ? 0 : 52)}\">{X(l)}</tspan>"));

    double titleBlockH = titleLines.Count * 52;
    double titleY = 280 - titleBlockH / 2;
    string zhShort = titleZh.Length > 20 ? titleZh[..20] + "…" : titleZh;

    return
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"600\" height=\"900\" viewBox=\"0 0 600 900\">" +
        $"<rect width=\"600\" height=\"900\" fill=\"{bg}\"/>" +
        $"<rect x=\"0\" y=\"0\" width=\"600\" height=\"8\" fill=\"{accent}\"/>" +
        $"<rect x=\"60\" y=\"60\" width=\"480\" height=\"2\" fill=\"{accent}\" opacity=\"0.4\"/>" +
        $"<rect x=\"60\" y=\"840\" width=\"480\" height=\"2\" fill=\"{accent}\" opacity=\"0.4\"/>" +
        $"<circle cx=\"300\" cy=\"450\" r=\"260\" fill=\"none\" stroke=\"{accent}\" stroke-width=\"1\" opacity=\"0.15\"/>" +
        $"<circle cx=\"300\" cy=\"450\" r=\"200\" fill=\"none\" stroke=\"{accent}\" stroke-width=\"1\" opacity=\"0.1\"/>" +
        $"<rect x=\"60\" y=\"{titleY - 30}\" width=\"480\" height=\"{titleBlockH + 60}\" fill=\"{accent}\" opacity=\"0.08\" rx=\"4\"/>" +
        $"<rect x=\"60\" y=\"{titleY - 30}\" width=\"4\" height=\"{titleBlockH + 60}\" fill=\"{accent}\" rx=\"2\"/>" +
        $"<text x=\"300\" y=\"{titleY}\" font-family=\"Georgia, serif\" font-size=\"44\" font-weight=\"bold\" " +
        $"fill=\"{fg}\" text-anchor=\"middle\" dominant-baseline=\"hanging\">{titleSvg}</text>" +
        $"<text x=\"300\" y=\"{titleY + titleBlockH + 24}\" font-family=\"serif\" font-size=\"22\" " +
        $"fill=\"{fg}\" text-anchor=\"middle\" opacity=\"0.6\">{X(zhShort)}</text>" +
        $"<line x1=\"220\" y1=\"580\" x2=\"380\" y2=\"580\" stroke=\"{accent}\" stroke-width=\"1.5\"/>" +
        $"<text x=\"300\" y=\"610\" font-family=\"Georgia, serif\" font-size=\"24\" font-weight=\"bold\" " +
        $"fill=\"{fg}\" text-anchor=\"middle\">{X(authorEn)}</text>" +
        $"<text x=\"300\" y=\"645\" font-family=\"serif\" font-size=\"18\" " +
        $"fill=\"{fg}\" text-anchor=\"middle\" opacity=\"0.6\">{X(authorZh)}</text>" +
        $"<text x=\"300\" y=\"870\" font-family=\"Georgia, serif\" font-size=\"13\" " +
        $"fill=\"{fg}\" text-anchor=\"middle\" opacity=\"0.35\">52shuku.net · Translated to English</text>" +
        "</svg>";
}

static List<string> WrapText(string text, int maxChars)
{
    var words = text.Split(' ');
    var lines = new List<string>();
    var cur   = new StringBuilder();
    foreach (var word in words)
    {
        if (cur.Length + word.Length + 1 > maxChars && cur.Length > 0)
        { lines.Add(cur.ToString()); cur.Clear(); }
        if (cur.Length > 0) cur.Append(' ');
        cur.Append(word);
    }
    if (cur.Length > 0) lines.Add(cur.ToString());
    return lines.Count > 0 ? lines : new List<string> { text };
}

static void W(ZipArchive zip, string name, string content)
{
    var e = zip.CreateEntry(name, CompressionLevel.Optimal);
    using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
    w.Write(content);
}

static string X(string s) => s.Replace("&","&amp;").Replace("<","&lt;").Replace(">","&gt;").Replace("\"","&quot;");


// BookInfo record — holds all metadata and page list for one novel
record BookInfo(string IndexUrl, string Title, string Author, List<string> PageUrls, int Total, string? CoverUrl)
{
    public int     PageLimit { get; init; } = 0;
    public string? TitleEn  { get; set; }
    public string? AuthorEn { get; set; }
}
