using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

if (args.Length == 0)
{
    Console.WriteLine("Usage: ShukuEpub <index-url> [pages] [output.epub]");
    Console.WriteLine("  pages = how many pages to download (0 = all)");
    Console.WriteLine("  e.g. ShukuEpub https://www.52shuku.net/bl/09_b/bkd7d.html 3");
    return;
}

string indexUrl  = args[0];
int    pageLimit = args.Length > 1 && int.TryParse(args[1], out int pl) ? pl : 0;
string? outFile  = args.Length > 2 && !string.IsNullOrWhiteSpace(args[2]) ? args[2] : null;
string? coverUrl = args.Length > 3 && !string.IsNullOrWhiteSpace(args[3]) ? args[3] : null;

// ── HTTP: site client ─────────────────────────────────────────────────────────
var sh = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
using var site = new HttpClient(sh) { Timeout = TimeSpan.FromSeconds(30) };
site.DefaultRequestHeaders.Add("User-Agent",
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
site.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9");
site.DefaultRequestHeaders.Add("Referer", "https://www.52shuku.net/");

// ── HTTP: Google Translate client (clean, no site headers) ───────────────────
var gh = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
using var gt = new HttpClient(gh) { Timeout = TimeSpan.FromSeconds(20) };
gt.DefaultRequestHeaders.Add("User-Agent",
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");

// ── Fetch page as properly decoded string ─────────────────────────────────────
async Task<string> Fetch(string url, int retries = 4)
{
    int delay = 1000;
    Exception? last = null;
    for (int i = 0; i <= retries; i++)
    {
        try
        {
            byte[] bytes = await site.GetByteArrayAsync(url);

            // Detect charset from meta tag in raw bytes (ASCII-safe scan)
            string ascii = Encoding.ASCII.GetString(bytes);
            var cm = Regex.Match(ascii, @"charset\s*=\s*[""']?\s*([\w-]+)", RegexOptions.IgnoreCase);
            string charset = cm.Success ? cm.Groups[1].Value.Trim() : "gbk";

            Encoding enc;
            try   { enc = Encoding.GetEncoding(charset); }
            catch { enc = Encoding.GetEncoding("gbk"); }

            return enc.GetString(bytes);
        }
        catch (Exception ex) { last = ex; await Task.Delay(delay); delay = Math.Min(delay*2, 16000); }
    }
    throw new Exception($"Fetch failed: {url} — {last?.Message}");
}

// ── Extract Chinese paragraphs using pure regex (no HtmlAgilityPack) ─────────
static List<string> ExtractParagraphs(string html)
{
    // Remove scripts and styles
    html = Regex.Replace(html, @"<script[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
    html = Regex.Replace(html, @"<style[\s\S]*?</style>",   "", RegexOptions.IgnoreCase);

    var result = new List<string>();

    // Match each <p>...</p> — non-greedy, single line
    foreach (Match m in Regex.Matches(html, @"<p(?:\s[^>]*)?>([^<]*(?:<(?!/p>)[^<]*)*)</p>", RegexOptions.IgnoreCase))
    {
        string inner = m.Groups[1].Value;
        // Strip remaining tags
        inner = Regex.Replace(inner, @"<[^>]+>", "");
        // Decode entities
        inner = inner.Replace("&nbsp;", " ").Replace("&amp;", "&")
                     .Replace("&lt;", "<").Replace("&gt;", ">")
                     .Replace("&quot;", "\"").Replace("&#39;", "'")
                     .Replace("\u3000", " ").Trim();

        // Only keep lines with actual Chinese characters
        if (inner.Length > 0 && Regex.IsMatch(inner, @"[\u4e00-\u9fff]"))
            result.Add(inner);
    }
    return result;
}

// ── Google Translate ──────────────────────────────────────────────────────────
async Task<string> Translate(string text, int retries = 4)
{
    if (string.IsNullOrWhiteSpace(text)) return text;

    // Chunk by lines, max 1500 chars each
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

    var parts = new List<string>();
    foreach (var chunk in chunks)
    {
        int delay = 1500;
        string translated = chunk; // fallback
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
                if (!string.IsNullOrEmpty(r)) { translated = r; break; }
            }
            catch (Exception ex) when (attempt < retries)
            {
                Console.WriteLine($"\n  [translate retry {attempt+1}] {ex.Message}");
                await Task.Delay(delay);
                delay = Math.Min(delay * 2, 10000);
            }
            catch (Exception ex) { Console.WriteLine($"\n  [translate failed] {ex.Message}"); break; }
        }
        parts.Add(translated);
        await Task.Delay(150);
    }
    return string.Join("\n", parts);
}

// ── Parse index ───────────────────────────────────────────────────────────────
Console.WriteLine($"Fetching index: {indexUrl}");
string indexHtml = await Fetch(indexUrl);

string title = Regex.Match(indexHtml, @"<h1[^>]*>([\s\S]*?)</h1>", RegexOptions.IgnoreCase).Groups[1].Value;
title = Regex.Replace(title, @"<[^>]+>", "").Trim();
title = Regex.Replace(title, @"\s*\(\d+\)\s*$", "").Trim();
if (string.IsNullOrWhiteSpace(title))
    title = Regex.Match(indexUrl, @"/([^/]+)\.html$").Groups[1].Value;

string author = "Unknown";
var am = Regex.Match(indexHtml, @"作者[：:]\s*([^\s【\n】<&]+)");
if (am.Success) author = am.Groups[1].Value.Trim();

Console.WriteLine($"Title:  {title}");
Console.WriteLine($"Author: {author}");

// Translate title and author to English
Console.Write("Translating title...");
string titleEn  = await Translate(title);
string authorEn = await Translate(author);
Console.WriteLine($"\nTitle (EN):  {titleEn}");
Console.WriteLine($"Author (EN): {authorEn}");

string baseUrl = Regex.Replace(indexUrl, @"\.html$", "");
var pageUrls = Regex.Matches(indexHtml, @"href=[""']?([^""'\s>]+)[""']?", RegexOptions.IgnoreCase)
    .Select(m => m.Groups[1].Value)
    .Select(h => h.StartsWith("http") ? h : new Uri(new Uri(indexUrl), h).ToString())
    .Where(u => u.StartsWith(baseUrl + "_") && u.EndsWith(".html"))
    .Distinct()
    .OrderBy(u => { var m = Regex.Match(u, @"_(\d+)\.html$"); return m.Success ? int.Parse(m.Groups[1].Value) : 0; })
    .ToList();

int total = pageLimit > 0 ? Math.Min(pageLimit, pageUrls.Count) : pageUrls.Count;
Console.WriteLine($"Found {pageUrls.Count} pages. Downloading {total}.\n");
if (total == 0) { Console.WriteLine("No pages found."); return; }

// ── Download + translate ──────────────────────────────────────────────────────
var chapters = new List<(int Idx, string Text)>();
var t0 = DateTime.Now;

for (int i = 0; i < total; i++)
{
    double elapsed = (DateTime.Now - t0).TotalSeconds;
    string eta = i > 0 ? $"~{TimeSpan.FromSeconds(elapsed/i*(total-i)):mm\\:ss} left" : "";

    Console.Write($"\r[{i+1}/{total}] Fetching...    {eta}      ");
    string pageHtml = await Fetch(pageUrls[i]);

    var paras = ExtractParagraphs(pageHtml);

    // Debug: show first paragraph raw to confirm it's Chinese
    if (i == 0 && paras.Count > 0)
        Console.WriteLine($"\n  [debug] first para ({paras[0].Length} chars): {paras[0][..Math.Min(60,paras[0].Length)]}");

    string chinese = string.Join("\n", paras);

    Console.Write($"\r[{i+1}/{total}] Translating... {eta}      ");
    string english = await Translate(chinese);

    chapters.Add((i + 1, english));
    await Task.Delay(300);
}

// ── Build EPUB ────────────────────────────────────────────────────────────────
Console.WriteLine("\n\nBuilding EPUB...");

if (outFile == null)
{
    string safeName = Regex.Replace(titleEn, @"[\\/:*?""<>|]", "_").Trim('_');
    if (string.IsNullOrWhiteSpace(safeName))
        safeName = Regex.Match(indexUrl, @"/([^/]+)\.html$").Groups[1].Value;
    string fileName = safeName[..Math.Min(safeName.Length, 60)] +
                      (pageLimit > 0 ? $"_pages1-{total}" : "") + ".epub";
    // Save to user's Downloads folder — no admin rights needed
    string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    outFile = Path.Combine(downloads, fileName);
}

if (File.Exists(outFile)) File.Delete(outFile);

// ── Download cover image if URL provided, otherwise generate SVG ─────────────
byte[]? coverBytes = null;
string  coverMime  = "image/jpeg";

if (!string.IsNullOrWhiteSpace(coverUrl))
{
    Console.Write($"Downloading cover from {coverUrl} ...");
    try
    {
        coverBytes = await gt.GetByteArrayAsync(coverUrl);
        // Detect mime type from URL extension or magic bytes
        string ext = Path.GetExtension(new Uri(coverUrl).AbsolutePath).ToLowerInvariant();
        coverMime = ext switch
        {
            ".png"  => "image/png",
            ".gif"  => "image/gif",
            ".webp" => "image/webp",
            _       => "image/jpeg"
        };
        // Double-check with magic bytes
        if (coverBytes.Length >= 4)
        {
            if (coverBytes[0] == 0x89 && coverBytes[1] == 0x50) coverMime = "image/png";
            else if (coverBytes[0] == 0xFF && coverBytes[1] == 0xD8) coverMime = "image/jpeg";
            else if (coverBytes[0] == 0x47 && coverBytes[1] == 0x49) coverMime = "image/gif";
        }
        Console.WriteLine($" OK ({coverBytes.Length / 1024}KB, {coverMime})");
    }
    catch (Exception ex)
    {
        Console.WriteLine($" Failed: {ex.Message}");
        Console.WriteLine("  Falling back to generated cover.");
        coverBytes = null;
    }
}

BuildEpub(outFile, title, titleEn, author, authorEn, chapters, coverBytes, coverMime);
Console.WriteLine($"Saved: {Path.GetFullPath(outFile)}");

// ── EPUB builder ──────────────────────────────────────────────────────────────
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

    // ── Cover image ───────────────────────────────────────────────────────────
    string coverItemId   = "cover-image";
    string coverExt      = coverMime == "image/png" ? "png" : coverMime == "image/gif" ? "gif" : "jpg";
    string coverImgFname = $"cover.{coverExt}";
    string coverMimeAttr = coverMime;

    if (coverBytes != null)
    {
        // Write downloaded image bytes
        var ce = zip.CreateEntry($"OEBPS/{coverImgFname}", CompressionLevel.NoCompression);
        using var cs = ce.Open();
        cs.Write(coverBytes, 0, coverBytes.Length);
    }
    else
    {
        // Generate SVG cover
        coverImgFname = "cover.svg";
        coverMimeAttr = "image/svg+xml";
        W(zip, "OEBPS/cover.svg", GenerateCoverSvg(titleEn, titleZh, authorEn, authorZh));
    }

    // Cover XHTML wrapper
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

    // ── Title / info page ─────────────────────────────────────────────────────
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

    // ── Chapter pages ─────────────────────────────────────────────────────────
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

    // cover-image manifest item (special role for EPUB readers)
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

// ── Generate SVG cover ────────────────────────────────────────────────────────
static string GenerateCoverSvg(string titleEn, string titleZh, string authorEn, string authorZh)
{
    // Pick a color palette based on title hash for variety
    var palettes = new[]
    {
        ("#1a1a2e", "#e94560", "#ffffff"), // dark navy / red
        ("#0f3460", "#e94560", "#ffffff"), // deep blue / red
        ("#16213e", "#0f3460", "#e2b96f"), // navy / gold
        ("#2d1b69", "#11998e", "#ffffff"), // purple / teal
        ("#1a1a1a", "#c0392b", "#f5f5f5"), // dark / crimson
        ("#0d2137", "#f7971e", "#ffffff"), // dark / orange
    };
    int pick = Math.Abs(titleEn.GetHashCode()) % palettes.Length;
    var (bg, accent, fg) = palettes[pick];

    // Wrap title lines (max ~22 chars per line)
    var titleLines = WrapText(titleEn, 22);
    var titleSvg = string.Join("", titleLines.Select((l, i) =>
        $"<tspan x=\"300\" dy=\"{(i == 0 ? 0 : 52)}\">{X(l)}</tspan>"));

    double titleBlockH = titleLines.Count * 52;
    double titleY = 280 - titleBlockH / 2;

    // Short Chinese title (one line, truncated)
    string zhShort = titleZh.Length > 20 ? titleZh[..20] + "…" : titleZh;

    return
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"600\" height=\"900\" viewBox=\"0 0 600 900\">" +

        // Background
        $"<rect width=\"600\" height=\"900\" fill=\"{bg}\"/>" +

        // Decorative top bar
        $"<rect x=\"0\" y=\"0\" width=\"600\" height=\"8\" fill=\"{accent}\"/>" +

        // Decorative accent lines
        $"<rect x=\"60\" y=\"60\" width=\"480\" height=\"2\" fill=\"{accent}\" opacity=\"0.4\"/>" +
        $"<rect x=\"60\" y=\"840\" width=\"480\" height=\"2\" fill=\"{accent}\" opacity=\"0.4\"/>" +

        // Large decorative circle (background element)
        $"<circle cx=\"300\" cy=\"450\" r=\"260\" fill=\"none\" stroke=\"{accent}\" stroke-width=\"1\" opacity=\"0.15\"/>" +
        $"<circle cx=\"300\" cy=\"450\" r=\"200\" fill=\"none\" stroke=\"{accent}\" stroke-width=\"1\" opacity=\"0.1\"/>" +

        // Accent rectangle behind title
        $"<rect x=\"60\" y=\"{titleY - 30}\" width=\"480\" height=\"{titleBlockH + 60}\" fill=\"{accent}\" opacity=\"0.08\" rx=\"4\"/>" +
        $"<rect x=\"60\" y=\"{titleY - 30}\" width=\"4\" height=\"{titleBlockH + 60}\" fill=\"{accent}\" rx=\"2\"/>" +

        // Title (English)
        $"<text x=\"300\" y=\"{titleY}\" font-family=\"Georgia, serif\" font-size=\"44\" font-weight=\"bold\" " +
        $"fill=\"{fg}\" text-anchor=\"middle\" dominant-baseline=\"hanging\">{titleSvg}</text>" +

        // Chinese title
        $"<text x=\"300\" y=\"{titleY + titleBlockH + 24}\" font-family=\"serif\" font-size=\"22\" " +
        $"fill=\"{fg}\" text-anchor=\"middle\" opacity=\"0.6\">{X(zhShort)}</text>" +

        // Divider
        $"<line x1=\"220\" y1=\"580\" x2=\"380\" y2=\"580\" stroke=\"{accent}\" stroke-width=\"1.5\"/>" +

        // Author (English)
        $"<text x=\"300\" y=\"610\" font-family=\"Georgia, serif\" font-size=\"24\" font-weight=\"bold\" " +
        $"fill=\"{fg}\" text-anchor=\"middle\">{X(authorEn)}</text>" +

        // Author (Chinese)
        $"<text x=\"300\" y=\"645\" font-family=\"serif\" font-size=\"18\" " +
        $"fill=\"{fg}\" text-anchor=\"middle\" opacity=\"0.6\">{X(authorZh)}</text>" +

        // Bottom label
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
