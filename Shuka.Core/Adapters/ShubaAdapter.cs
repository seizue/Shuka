using System.Text.RegularExpressions;

namespace Shuka.Core.Adapters;

/// <summary>
/// Adapter for 69shuba.com (69书吧) — a popular Simplified Chinese novel site.
///
/// Index URL formats accepted:
///   https://www.69shuba.com/book/{bookId}.htm   (info page, only shows last 5 chapters)
///   https://www.69shuba.com/book/{bookId}/       (full chapter list page)
///
/// Chapter URL format:
///   https://www.69shuba.com/txt/{bookId}/{chapterId}
///
/// The site uses GBK encoding — handled automatically by HttpFetcher's
/// charset auto-detection from the HTML meta tag.
/// The site returns 403 on direct HTTP requests; the HttpFetcher Cloudflare/
/// Playwright bypass handles this transparently.
/// </summary>
public class ShubaAdapter : ISiteAdapter
{
    public string SiteName => "69shuba.com";
    public bool RequiresCfBypass => true;

    public bool Matches(string url) =>
        url.Contains("69shuba.com", StringComparison.OrdinalIgnoreCase);

    public string NormalizeUrl(string url)
    {
        if (!url.StartsWith("http")) url = "https://" + url;

        // If user pastes a chapter URL (/txt/{bookId}/{chapterId}), redirect to index
        var chapterM = Regex.Match(url,
            @"https?://(?:www\.)?69shuba\.com/txt/(\d+)/\d+",
            RegexOptions.IgnoreCase);
        if (chapterM.Success)
            return $"https://www.69shuba.com/book/{chapterM.Groups[1].Value}/";

        // Normalise .htm info page → trailing-slash full chapter list page
        // /book/90488.htm  →  /book/90488/
        var infoM = Regex.Match(url,
            @"https?://(?:www\.)?69shuba\.com/book/(\d+)\.htm",
            RegexOptions.IgnoreCase);
        if (infoM.Success)
            return $"https://www.69shuba.com/book/{infoM.Groups[1].Value}/";

        // Already the full list page — strip any query/fragment
        var listM = Regex.Match(url,
            @"(https?://(?:www\.)?69shuba\.com/book/\d+/)",
            RegexOptions.IgnoreCase);
        return listM.Success ? listM.Groups[1].Value : url;
    }

    public IndexInfo ParseIndex(string html, string indexUrl)
    {
        // ── Book ID ───────────────────────────────────────────────────────────
        string bookId = Regex.Match(indexUrl, @"/book/(\d+)/").Groups[1].Value;

        // Strip scripts/styles to avoid matching nav/ad text
        string cleanHtml = Regex.Replace(html, @"<script[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
        cleanHtml = Regex.Replace(cleanHtml, @"<style[\s\S]*?</style>", "", RegexOptions.IgnoreCase);

        // ── Title ─────────────────────────────────────────────────────────────
        // Prefer <h1 class="bookname"> — take only direct text before any child tag
        string title = "";
        var h1m = Regex.Match(cleanHtml, @"<h1[^>]*class=""[^""]*bookname[^""]*""[^>]*>\s*([^<]+)", RegexOptions.IgnoreCase);
        if (h1m.Success) title = h1m.Groups[1].Value.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            h1m = Regex.Match(cleanHtml, @"<h1[^>]*>\s*([^<]+)", RegexOptions.IgnoreCase);
            if (h1m.Success) title = h1m.Groups[1].Value.Trim();
        }
        // Try og:title (most reliable — contains just the book name)
        if (string.IsNullOrWhiteSpace(title))
        {
            var ogT = Regex.Match(cleanHtml, @"<meta[^>]+property=""og:title""[^>]+content=""([^""]+)""", RegexOptions.IgnoreCase);
            if (!ogT.Success) ogT = Regex.Match(cleanHtml, @"<meta[^>]+content=""([^""]+)""[^>]+property=""og:title""", RegexOptions.IgnoreCase);
            if (ogT.Success) title = ogT.Groups[1].Value.Trim();
        }
        if (string.IsNullOrWhiteSpace(title))
            title = Regex.Match(cleanHtml, @"<title[^>]*>([^<|_–\-]+)", RegexOptions.IgnoreCase).Groups[1].Value.Trim();
        // Strip common site suffixes appended to titles (SEO junk)
        title = Regex.Replace(title, @"\s*[,，]\s*.*$", "").Trim();
        title = Regex.Replace(title, @"\s*[-_|–]\s*.*$", "").Trim();
        title = Regex.Replace(title, @"\s*(最新章节|无弹窗|全文阅读|免费阅读).*$", "").Trim();

        // ── Author ────────────────────────────────────────────────────────────
        // 作者：<a ...>name</a>  or  作者：name
        string author = "Unknown";
        var am = Regex.Match(html,
            @"作者[：:]\s*<a[^>]*>([^<]+)</a>",
            RegexOptions.IgnoreCase);
        if (!am.Success)
            am = Regex.Match(html, @"作者[：:]\s*([^\s<\n,，]+)");
        if (am.Success) author = am.Groups[1].Value.Trim();

        // ── Chapter list ──────────────────────────────────────────────────────
        // Links: href="/txt/{bookId}/{chapterId}"  or  href="https://www.69shuba.com/txt/{bookId}/{chapterId}"
        var chapterMatches = Regex.Matches(html,
            @"href=[""'](?:https?://(?:www\.)?69shuba\.com)?/txt/" + Regex.Escape(bookId) + @"/(\d+)[""'][^>]*>([^<]*)</a>",
            RegexOptions.IgnoreCase);

        var chapters = chapterMatches
            .Cast<Match>()
            .Select(m => new
            {
                ChapterId = m.Groups[1].Value,
                Title     = System.Net.WebUtility.HtmlDecode(m.Groups[2].Value.Trim())
            })
            .DistinctBy(x => x.ChapterId)
            .Select((x, i) => new ChapterRef(
                $"https://www.69shuba.com/txt/{bookId}/{x.ChapterId}",
                string.IsNullOrWhiteSpace(x.Title) ? $"Chapter {i + 1}" : x.Title))
            .ToList();

        // ── Cover ─────────────────────────────────────────────────────────────
        // Try og:image / img tag in the chapter list page first
        string? cover = null;
        var imgM = Regex.Match(html,
            @"<img[^>]+src=[""'](https?://[^""']*/" + Regex.Escape(bookId) + @"[^""']*\.(jpg|png|webp))[""']",
            RegexOptions.IgnoreCase);
        if (imgM.Success) cover = imgM.Groups[1].Value.Trim();

        if (cover == null)
        {
            var ogM = Regex.Match(html,
                @"<meta[^>]+property=[""']og:image[""'][^>]+content=[""']([^""']+)[""']",
                RegexOptions.IgnoreCase);
            if (!ogM.Success)
                ogM = Regex.Match(html,
                    @"<meta[^>]+content=[""']([^""']+)[""'][^>]+property=[""']og:image[""']",
                    RegexOptions.IgnoreCase);
            if (ogM.Success) cover = ogM.Groups[1].Value.Trim();
        }

        // Fallback: construct the CDN cover URL directly from the book ID.
        // 69shuba stores covers at cdn.cdnshu.com/files/article/image/{prefix}/{bookId}/{bookId}s.jpg
        // where prefix = floor(bookId / 1000).
        if (cover == null && int.TryParse(bookId, out int bid))
        {
            int prefix = bid / 1000;
            cover = $"https://cdn.cdnshu.com/files/article/image/{prefix}/{bookId}/{bookId}s.jpg";
        }

        return new IndexInfo(title, author, chapters, cover);
    }

    public List<string> ExtractChapterText(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return new List<string>();

        // Remove noise blocks first
        html = Regex.Replace(html, @"<script[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<style[\s\S]*?</style>",   "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<nav[\s\S]*?</nav>",       "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<header[\s\S]*?</header>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<footer[\s\S]*?</footer>", "", RegexOptions.IgnoreCase);

        // 69shuba chapter content:
        // Main container is <div class="txtnav">.
        // Inside .txtnav, the layout is:
        //   1. <div class="txtinfo hide720">...</div> (breadcrumb navigation)
        //   2. <div class="txtright" id="txtright">...</div> (font/bookmark buttons)
        //   3. <h1>Chapter Title</h1>
        //   4. <div class="contentadv">...</div> (ad banner)
        //   5. [Novel paragraphs separated by <br><br> or <p>]
        //   6. <div class="page1">...</div> (bottom prev/next chapter navigation)
        string? content = null;

        var txtNavMatch = Regex.Match(html, @"<div[^>]+class=""[^""]*txtnav[^""]*""[^>]*>([\s\S]+)", RegexOptions.IgnoreCase);
        if (txtNavMatch.Success)
        {
            string raw = txtNavMatch.Groups[1].Value;

            // Cut off at the bottom pagination / tools / footer
            var endMatch = Regex.Match(raw, @"<div[^>]+class=""[^""]*(?:page1|bottom-ad|txtbottom|bottom_tools)[^""]*""", RegexOptions.IgnoreCase);
            if (endMatch.Success)
            {
                raw = raw[..endMatch.Index];
            }
            else
            {
                var closeMatch = Regex.Match(raw, @"</div>\s*</div>", RegexOptions.IgnoreCase);
                if (closeMatch.Success && closeMatch.Index > 200)
                    raw = raw[..closeMatch.Index];
            }

            // In 69shuba, <h1> is placed directly above the story text (below txtinfo and txtright).
            // Taking everything after </h1> eliminates all header and navigation noise cleanly.
            int h1End = raw.IndexOf("</h1>", StringComparison.OrdinalIgnoreCase);
            if (h1End >= 0 && (raw.Length - h1End) > 200)
            {
                content = raw[(h1End + 5)..];
            }
            else
            {
                content = raw;
            }
        }

        // Secondary / fallback containers if .txtnav was not matched
        if (content == null)
        {
            foreach (var pattern in new[]
            {
                @"<div[^>]+id=""content""[^>]*>([\s\S]+?)</div>\s*</div>",
                @"<div[^>]+id=""content""[^>]*>([\s\S]+)",
                @"<div[^>]+class=""[^""]*\bcontent\b[^""]*""[^>]*>([\s\S]+)",
                @"<div[^>]+class=""[^""]*read_content[^""]*""[^>]*>([\s\S]+)",
                @"<div[^>]+id=""htmlContent""[^>]*>([\s\S]+)",
                @"<div[^>]+id=""txt""[^>]*>([\s\S]+)",
                @"<article[^>]*>([\s\S]+?)</article>",
            })
            {
                var m = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
                if (m.Success && m.Groups[1].Value.Length > 200)
                {
                    content = m.Groups[1].Value;
                    break;
                }
            }
        }

        content ??= html;

        // Clean out any embedded UI/ad containers (run multiple passes to collapse nested divs)
        for (int pass = 0; pass < 3; pass++)
        {
            content = Regex.Replace(content, @"<div[^>]+class=""[^""]*txtinfo[^""]*""[^>]*>[\s\S]*?</div>", "", RegexOptions.IgnoreCase);
            content = Regex.Replace(content, @"<div[^>]+class=""[^""]*hide720[^""]*""[^>]*>[\s\S]*?</div>", "", RegexOptions.IgnoreCase);
            content = Regex.Replace(content, @"<div[^>]+(?:class=""[^""]*txtright[^""]*""|id=""txtright"")[^>]*>[\s\S]*?</div>", "", RegexOptions.IgnoreCase);
            content = Regex.Replace(content, @"<div[^>]+class=""[^""]*(?:contentadv|bottom-ad|err_tips|page1|txtbottom|bottom_tools)[^""]*""[^>]*>[\s\S]*?</div>", "", RegexOptions.IgnoreCase);
        }
        content = Regex.Replace(content, @"<h1[^>]*>[\s\S]*?</h1>", "", RegexOptions.IgnoreCase);

        // Convert <br> and <p> to newlines, strip remaining tags
        content = Regex.Replace(content, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        content = Regex.Replace(content, @"</?p[^>]*>",  "\n", RegexOptions.IgnoreCase);
        content = Regex.Replace(content, @"</?div[^>]*>", "\n", RegexOptions.IgnoreCase);
        content = Regex.Replace(content, @"<[^>]+>",   "");
        content = System.Net.WebUtility.HtmlDecode(content);

        var result = new List<string>();
        foreach (var line in content.Split('\n'))
        {
            string trimmed = line.Trim('\r', ' ', '\t', '\u3000', '\u00A0', '\u2002', '\u2003', '\u2009', '\uFEFF');
            if (trimmed.Length == 0) continue;

            // Filter out common site watermarks and URLs
            if (trimmed.Contains("69shuba", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("69shu", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("www.", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(trimmed, @"https?://", RegexOptions.IgnoreCase))
            {
                continue;
            }

            // Keep lines with CJK characters or Chinese dialogue/punctuation
            if (Regex.IsMatch(trimmed, @"[\u4e00-\u9fff\u3400-\u4dbf\uf900-\ufaff]") ||
                (trimmed.Length >= 2 && Regex.IsMatch(trimmed, @"^[""“”'‘’（(].*[""“”'‘’）)…?!？！~]$")))
            {
                result.Add(trimmed);
            }
        }

        // Fallback: if CJK filter yielded 0 lines, keep non-watermark prose lines > 15 chars
        if (result.Count == 0)
        {
            foreach (var line in content.Split('\n'))
            {
                string trimmed = line.Trim('\r', ' ', '\t', '\u3000', '\u00A0', '\u2002', '\u2003', '\u2009', '\uFEFF');
                if (trimmed.Length > 15 &&
                    !trimmed.Contains("69shuba", StringComparison.OrdinalIgnoreCase) &&
                    !trimmed.Contains("69shu", StringComparison.OrdinalIgnoreCase) &&
                    !trimmed.Contains("www.", StringComparison.OrdinalIgnoreCase) &&
                    !Regex.IsMatch(trimmed, @"https?://", RegexOptions.IgnoreCase))
                {
                    result.Add(trimmed);
                }
            }
        }

        return result;
    }
}
