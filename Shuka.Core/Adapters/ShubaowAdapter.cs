using System.Text.RegularExpressions;

namespace Shuka.Core.Adapters;

/// <summary>
/// Adapter for shubaow.net (书宝网) — popular Simplified Chinese romance, BL, GL, and general novel site.
///
/// Index URL format:
///   https://www.shubaow.net/book/{bookId}.html
///
/// Chapter URL format:
///   https://www.shubaow.net/book/{bookId}/{chapterId}.html
///
/// Encoding: GBK / GB2312 — auto-detected by HttpFetcher.
/// Accessible directly without Cloudflare protection.
/// </summary>
public class ShubaowAdapter : ISiteAdapter
{
    public string SiteName => "shubaow.net";
    public bool RequiresCfBypass => false;

    public bool Matches(string url) =>
        url.Contains("shubaow.net", StringComparison.OrdinalIgnoreCase);

    public string NormalizeUrl(string url)
    {
        if (!url.StartsWith("http")) url = "https://" + url;

        // If chapter URL (/book/{bookId}/{chapterId}.html), redirect to book index page
        var chapterM = Regex.Match(url,
            @"https?://(?:www\.)?shubaow\.net/book/(\d+)/\d+\.html",
            RegexOptions.IgnoreCase);
        if (chapterM.Success)
            return $"https://www.shubaow.net/book/{chapterM.Groups[1].Value}.html";

        // Normalise path /book/{bookId}.html
        var infoM = Regex.Match(url,
            @"https?://(?:www\.)?shubaow\.net/book/(\d+)\.html",
            RegexOptions.IgnoreCase);
        if (infoM.Success)
            return $"https://www.shubaow.net/book/{infoM.Groups[1].Value}.html";

        // Category/folder numeric path /1/{bookId}/ -> /book/{bookId}.html
        var folderM = Regex.Match(url,
            @"https?://(?:www\.)?shubaow\.net/\d+/(\d+)/?",
            RegexOptions.IgnoreCase);
        if (folderM.Success)
            return $"https://www.shubaow.net/book/{folderM.Groups[1].Value}.html";

        return url;
    }

    public IndexInfo ParseIndex(string html, string indexUrl)
    {
        // ── Book ID ───────────────────────────────────────────────────────────
        string bookId = Regex.Match(indexUrl, @"/book/(\d+)\.html").Groups[1].Value;

        // Clean HTML to avoid matching scripts/styles
        string cleanHtml = Regex.Replace(html, @"<script[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
        cleanHtml = Regex.Replace(cleanHtml, @"<style[\s\S]*?</style>", "", RegexOptions.IgnoreCase);

        // ── Title ─────────────────────────────────────────────────────────────
        string title = "";
        var titleM = Regex.Match(cleanHtml, @"<h1[^>]*class=""[^""]*book-title-meta[^""]*""[^>]*>\s*([^<]+)", RegexOptions.IgnoreCase);
        if (titleM.Success) title = titleM.Groups[1].Value.Trim();

        if (string.IsNullOrWhiteSpace(title))
        {
            titleM = Regex.Match(cleanHtml, @"<h1[^>]*>\s*([^<]+)", RegexOptions.IgnoreCase);
            if (titleM.Success) title = titleM.Groups[1].Value.Trim();
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            var ogT = Regex.Match(cleanHtml, @"<meta[^>]+property=""og:title""[^>]+content=""([^""]+)""", RegexOptions.IgnoreCase);
            if (!ogT.Success) ogT = Regex.Match(cleanHtml, @"<meta[^>]+content=""([^""]+)""[^>]+property=""og:title""", RegexOptions.IgnoreCase);
            if (ogT.Success) title = ogT.Groups[1].Value.Trim();
        }

        if (string.IsNullOrWhiteSpace(title))
            title = Regex.Match(cleanHtml, @"<title[^>]*>([^<|_–\-]+)", RegexOptions.IgnoreCase).Groups[1].Value.Trim();

        // Strip common site suffixes
        title = Regex.Replace(title, @"\s*[,，/_–\-]\s*.*$", "").Trim();
        title = Regex.Replace(title, @"\s*(最新章节|无弹窗|全文阅读|免费阅读|书宝网).*$", "").Trim();

        // ── Author ────────────────────────────────────────────────────────────
        string author = "Unknown";
        var am = Regex.Match(cleanHtml, @"作者[：:]\s*([^\s<,\n]+)", RegexOptions.IgnoreCase);
        if (!am.Success)
            am = Regex.Match(cleanHtml, @"作者[：:]\s*<a[^>]*>([^<]+)</a>", RegexOptions.IgnoreCase);
        if (am.Success) author = am.Groups[1].Value.Trim();

        // ── Chapter List ──────────────────────────────────────────────────────
        // Extract only the chapter list container (id="list-chapterAll") to avoid
        // picking up the "latest episode" link in the page header, which would
        // place the newest chapter before chapter 1.
        string chapterHtml = cleanHtml;
        var listMatch = Regex.Match(cleanHtml,
            @"<div[^>]+id=""list-chapterAll""[^>]*>([\s\S]+?)(?:</div>\s*</div>|\z)",
            RegexOptions.IgnoreCase);
        if (!listMatch.Success)
            listMatch = Regex.Match(cleanHtml,
                @"<div[^>]+class=""[^""]*chapter-list-grid[^""]*""[^>]*>([\s\S]+?)(?:</div>\s*</div>|\z)",
                RegexOptions.IgnoreCase);
        if (listMatch.Success)
            chapterHtml = listMatch.Groups[1].Value;

        // Links: href="/book/{bookId}/{chapterId}.html"
        var chapterMatches = Regex.Matches(chapterHtml,
            @"href=[""'](?:https?://(?:www\.)?shubaow\.net)?/book/" + Regex.Escape(bookId) + @"/(\d+)\.html[""'][^>]*>\s*([^<]*)\s*</a>",
            RegexOptions.IgnoreCase);

        var chapters = chapterMatches
            .Cast<Match>()
            .Select(m => new
            {
                ChapterId = m.Groups[1].Value,
                Title = System.Net.WebUtility.HtmlDecode(m.Groups[2].Value.Trim())
            })
            .DistinctBy(x => x.ChapterId)
            .OrderBy(x => int.Parse(x.ChapterId)) // Sort by numeric chapter ID to ensure correct order
            .Select((x, i) => new ChapterRef(
                $"https://www.shubaow.net/book/{bookId}/{x.ChapterId}.html",
                string.IsNullOrWhiteSpace(x.Title) ? $"Chapter {i + 1}" : x.Title))
            .ToList();

        // ── Cover ─────────────────────────────────────────────────────────────
        string? cover = null;
        var ogM = Regex.Match(cleanHtml, @"<meta[^>]+property=[""']og:image[""'][^>]+content=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        if (ogM.Success) cover = ogM.Groups[1].Value.Trim();

        return new IndexInfo(title, author, chapters, cover);
    }

    public List<string> ExtractChapterText(string html)
    {
        // Strip non-content blocks
        html = Regex.Replace(html, @"<script[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<style[\s\S]*?</style>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<nav[\s\S]*?</nav>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<header[\s\S]*?</header>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<footer[\s\S]*?</footer>", "", RegexOptions.IgnoreCase);

        string? content = null;
        foreach (var pattern in new[]
        {
            @"<div[^>]+id=""htmlContent""[^>]*>([\s\S]+?)</div>\s*</div>",
            @"<div[^>]+id=""htmlContent""[^>]*>([\s\S]+)",
            @"<div[^>]+class=""[^""]*read-content-body[^""]*""[^>]*>([\s\S]+?)</div>",
            @"<div[^>]+class=""[^""]*read-content-body[^""]*""[^>]*>([\s\S]+)",
        })
        {
            var m = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
            if (m.Success && m.Groups[1].Value.Length > 100)
            {
                content = m.Groups[1].Value;
                break;
            }
        }

        content ??= html;

        // Convert breaks and paragraphs to newlines
        content = Regex.Replace(content, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        content = Regex.Replace(content, @"<p[^>]*>", "\n", RegexOptions.IgnoreCase);
        content = Regex.Replace(content, @"<[^>]+>", "");
        content = System.Net.WebUtility.HtmlDecode(content);

        var result = new List<string>();
        foreach (var line in content.Split('\n'))
        {
            string trimmed = line.Trim().TrimStart('\u3000').Trim();
            if (trimmed.Length > 0 &&
                Regex.IsMatch(trimmed, @"[\u4e00-\u9fff\u3400-\u4dbf\uf900-\ufaff]") &&
                !trimmed.Contains("shubaow") &&
                !trimmed.Contains("www.") &&
                !Regex.IsMatch(trimmed, @"https?://"))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }
}
