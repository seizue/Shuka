using System.Text.RegularExpressions;

namespace Shuka.Core.Adapters;

/// <summary>
/// Adapter for sto9.com (思兔閱讀) — popular Simplified/Traditional Chinese novel site.
///
/// Index URL formats accepted:
///   https://sto9.com/book/{bookId}.html         (main book info page)
///   https://sto9.com/book/{bookId}/index.html   (chapter list page)
///   https://sto9.com/txt/{bookId}/{chapterId}.html (chapter URL)
///   https://sto9.com/ajax_novels/chapterlist/{bookId}.html (complete chapter list endpoint)
///
/// Chapter URL format:
///   https://sto9.com/txt/{bookId}/{chapterId}.html
/// </summary>
public class Sto9Adapter : ISiteAdapter
{
    public string SiteName => "sto9.com";
    public bool RequiresCfBypass => false;

    public bool Matches(string url) =>
        url.Contains("sto9.com", StringComparison.OrdinalIgnoreCase);

    public string NormalizeUrl(string url)
    {
        if (!url.StartsWith("http")) url = "https://" + url;

        // If user pastes chapter URL (/txt/{bookId}/{chapterId}.html)
        var chapterM = Regex.Match(url, @"sto9\.com/txt/(\d+)/\d+\.html", RegexOptions.IgnoreCase);
        if (chapterM.Success)
            return $"https://sto9.com/book/{chapterM.Groups[1].Value}.html";

        // If user pastes chapterlist URL (/ajax_novels/chapterlist/{bookId}.html)
        var ajaxM = Regex.Match(url, @"sto9\.com/ajax_novels/chapterlist/(\d+)\.html", RegexOptions.IgnoreCase);
        if (ajaxM.Success)
            return $"https://sto9.com/book/{ajaxM.Groups[1].Value}.html";

        // If user pastes index / table of contents URL (/book/{bookId}/index.html)
        var indexM = Regex.Match(url, @"sto9\.com/book/(\d+)/index\.html", RegexOptions.IgnoreCase);
        if (indexM.Success)
            return $"https://sto9.com/book/{indexM.Groups[1].Value}.html";

        // If user pastes book URL (/book/{bookId}.html or /book/{bookId}/)
        var bookM = Regex.Match(url, @"sto9\.com/book/(\d+)(?:\.html|/|$)", RegexOptions.IgnoreCase);
        if (bookM.Success)
            return $"https://sto9.com/book/{bookM.Groups[1].Value}.html";

        return url;
    }

    public IndexInfo ParseIndex(string html, string indexUrl)
    {
        string bookId = Regex.Match(indexUrl, @"/book/(\d+)").Groups[1].Value;

        string cleanHtml = Regex.Replace(html, @"<script[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
        cleanHtml = Regex.Replace(cleanHtml, @"<style[\s\S]*?</style>", "", RegexOptions.IgnoreCase);

        // ── Title ─────────────────────────────────────────────────────────────
        string title = "";
        var ogT = Regex.Match(cleanHtml, @"<meta[^>]+property=[""']og:title[""'][^>]+content=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        if (!ogT.Success)
            ogT = Regex.Match(cleanHtml, @"<meta[^>]+content=[""']([^""']+)[""'][^>]+property=[""']og:title[""']", RegexOptions.IgnoreCase);
        if (ogT.Success) title = ogT.Groups[1].Value.Trim();

        if (string.IsNullOrWhiteSpace(title))
        {
            var h1m = Regex.Match(cleanHtml, @"<h1[^>]*>\s*<a[^>]*>([^<]+)</a>", RegexOptions.IgnoreCase);
            if (h1m.Success) title = h1m.Groups[1].Value.Trim();
        }

        if (string.IsNullOrWhiteSpace(title))
            title = Regex.Match(cleanHtml, @"<title[^>]*>([^<|_–\-]+)", RegexOptions.IgnoreCase).Groups[1].Value.Trim();

        title = Regex.Replace(title, @"\s*[,，]\s*.*$", "").Trim();
        title = Regex.Replace(title, @"\s*[-_|–]\s*.*$", "").Trim();
        title = Regex.Replace(title, @"\s*(最新章节|无弹窗|全文阅读|免费阅读|思兔sto9|思兔阅读).*$", "").Trim();

        // ── Author ────────────────────────────────────────────────────────────
        string author = "Unknown";
        var ogA = Regex.Match(cleanHtml, @"<meta[^>]+property=[""']og:novel:author[""'][^>]+content=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        if (ogA.Success) author = ogA.Groups[1].Value.Trim();

        if (author == "Unknown")
        {
            var am = Regex.Match(cleanHtml, @"作者[：:]\s*<a[^>]*>([^<]+)</a>", RegexOptions.IgnoreCase);
            if (!am.Success) am = Regex.Match(cleanHtml, @"作者[：:]\s*([^\s<\n,，]+)");
            if (am.Success) author = am.Groups[1].Value.Trim();
        }

        // ── Cover ─────────────────────────────────────────────────────────────
        string? cover = null;
        var ogI = Regex.Match(cleanHtml, @"<meta[^>]+property=[""']og:image[""'][^>]+content=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        if (ogI.Success) cover = ogI.Groups[1].Value.Trim();

        if (cover == null && int.TryParse(bookId, out int bid))
        {
            int prefix = bid / 1000;
            cover = $"https://sto9.com/files/article/image/{prefix}/{bookId}/{bookId}s.jpg";
        }

        // ── Chapters (initial chapters from the index page) ───────────────────
        var chapters = ParseChapterList(html, indexUrl);

        // sto9.com loads the complete chapter list dynamically via /ajax_novels/chapterlist/{bookId}.html
        string chapterListUrl = $"https://sto9.com/ajax_novels/chapterlist/{bookId}.html";

        return new IndexInfo(title, author, chapters, cover, ChapterListUrl: chapterListUrl);
    }

    public List<ChapterRef> ParseChapterList(string html, string listUrl)
    {
        var bookIdMatch = Regex.Match(listUrl, @"(?:book|chapterlist|txt)/(\d+)");
        string bookId = bookIdMatch.Success ? bookIdMatch.Groups[1].Value : "";

        string pattern = string.IsNullOrEmpty(bookId)
            ? @"href=[""'](?:https?://(?:www\.)?sto9\.com)?/txt/\d+/(\d+)\.html[""'][^>]*>([^<]*)</a>"
            : @"href=[""'](?:https?://(?:www\.)?sto9\.com)?/txt/" + Regex.Escape(bookId) + @"/(\d+)\.html[""'][^>]*>([^<]*)</a>";

        var matches = Regex.Matches(html, pattern, RegexOptions.IgnoreCase);

        return matches
            .Cast<Match>()
            .Select(m => new
            {
                ChapterId = m.Groups[1].Value,
                Title     = System.Net.WebUtility.HtmlDecode(m.Groups[2].Value.Trim())
            })
            .DistinctBy(x => x.ChapterId)
            .Select((x, i) => new ChapterRef(
                $"https://sto9.com/txt/{bookId}/{x.ChapterId}.html",
                string.IsNullOrWhiteSpace(x.Title) ? $"Chapter {i + 1}" : x.Title))
            .ToList();
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

        // Secondary / fallback containers
        if (content == null)
        {
            foreach (var pattern in new[]
            {
                @"<div[^>]+id=""content""[^>]*>([\s\S]+?)</div>\s*</div>",
                @"<div[^>]+id=""content""[^>]*>([\s\S]+)",
                @"<div[^>]+class=""[^""]*\bcontent\b[^""]*""[^>]*>([\s\S]+)",
                @"<div[^>]+class=""[^""]*read_content[^""]*""[^>]*>([\s\S]+)",
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

        // Clean out embedded UI/ad containers
        for (int pass = 0; pass < 3; pass++)
        {
            content = Regex.Replace(content, @"<div[^>]+class=""[^""]*(?:txtright|txtad|contentadv|bottom-ad|err_tips|page1|txtbottom|bottom_tools)[^""]*""[^>]*>[\s\S]*?</div>", "", RegexOptions.IgnoreCase);
            content = Regex.Replace(content, @"<ins[^>]*>[\s\S]*?</ins>", "", RegexOptions.IgnoreCase);
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
            if (trimmed.Contains("sto9", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("思兔", StringComparison.OrdinalIgnoreCase) ||
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
                    !trimmed.Contains("sto9", StringComparison.OrdinalIgnoreCase) &&
                    !trimmed.Contains("思兔", StringComparison.OrdinalIgnoreCase) &&
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

