using System.Text.RegularExpressions;

namespace Shuka.Core.Adapters;

/// <summary>
/// Adapter for quanben.io (全本小说网) — Simplified Chinese novel site.
///
/// Index URL formats accepted:
///   https://www.quanben.io/n/{bookId}/list.html   (chapter list page — redirected to AMP)
///   https://www.quanben.io/amp/n/{bookId}/list.html (AMP chapter list — fetched directly)
///   https://www.quanben.io/n/{bookId}/            (book info page)
///   https://www.quanben.io/n/{bookId}/{num}.html  (chapter page — redirected to AMP list)
///
/// The AMP list page is used for index fetching because the regular list page collapses
/// the full chapter list behind JavaScript (only showing ~24 at the start and end).
/// The AMP version contains all chapters in static HTML.
///
/// Chapter URL format (non-AMP, used for actual chapter downloads):
///   https://www.quanben.io/n/{bookId}/{chapterNum}.html
///
/// The site uses UTF-8 encoding. No Cloudflare protection.
/// </summary>
public class QuanbenAdapter : ISiteAdapter
{
    public string SiteName => "quanben.io";

    public bool Matches(string url) =>
        url.Contains("quanben.io", StringComparison.OrdinalIgnoreCase);

    public string NormalizeUrl(string url)
    {
        if (!url.StartsWith("http")) url = "https://" + url;

        // If user pastes a chapter URL (regular or AMP), redirect to the AMP list page
        var chapterM = Regex.Match(url,
            @"https?://(?:www\.)?quanben\.io/(?:amp/)?n/([^/?#]+)/(\d+)\.html",
            RegexOptions.IgnoreCase);
        if (chapterM.Success)
            return $"https://www.quanben.io/amp/n/{chapterM.Groups[1].Value}/list.html";

        // Normalise bare book URL → AMP list page
        var bookM = Regex.Match(url,
            @"https?://(?:www\.)?quanben\.io/(?:amp/)?n/([^/?#]+)/?$",
            RegexOptions.IgnoreCase);
        if (bookM.Success)
            return $"https://www.quanben.io/amp/n/{bookM.Groups[1].Value}/list.html";

        // Already a list page (regular or AMP) — normalize to AMP version
        var listM = Regex.Match(url,
            @"https?://(?:www\.)?quanben\.io/(?:amp/)?n/([^/?#]+)/list\.html",
            RegexOptions.IgnoreCase);
        if (listM.Success)
            return $"https://www.quanben.io/amp/n/{listM.Groups[1].Value}/list.html";

        return url;
    }

    public IndexInfo ParseIndex(string html, string indexUrl)
    {
        // Support both regular and AMP list URL formats
        string bookId = Regex.Match(indexUrl,
            @"/n/([^/?#]+)/list\.html", RegexOptions.IgnoreCase).Groups[1].Value;

        // Strip scripts/styles to avoid false matches
        string cleanHtml = Regex.Replace(html, @"<script[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
        cleanHtml = Regex.Replace(cleanHtml, @"<style[\s\S]*?</style>", "", RegexOptions.IgnoreCase);

        // ── Title ─────────────────────────────────────────────────────────────
        // quanben.io puts the title in <h1> or og:title
        string title = "";
        var h1m = Regex.Match(cleanHtml, @"<h1[^>]*>\s*([^<]+?)\s*</h1>", RegexOptions.IgnoreCase);
        if (h1m.Success) title = h1m.Groups[1].Value.Trim();

        if (string.IsNullOrWhiteSpace(title))
        {
            var ogT = Regex.Match(cleanHtml,
                @"<meta[^>]+property=[""']og:title[""'][^>]+content=[""']([^""']+)[""']",
                RegexOptions.IgnoreCase);
            if (!ogT.Success)
                ogT = Regex.Match(cleanHtml,
                    @"<meta[^>]+content=[""']([^""']+)[""'][^>]+property=[""']og:title[""']",
                    RegexOptions.IgnoreCase);
            if (ogT.Success) title = ogT.Groups[1].Value.Trim();
        }

        if (string.IsNullOrWhiteSpace(title))
            title = Regex.Match(cleanHtml, @"<title[^>]*>([^<|_–\-]+)",
                RegexOptions.IgnoreCase).Groups[1].Value.Trim();

        // Strip common SEO suffixes
        title = Regex.Replace(title, @"\s*[-_|–]\s*.*$", "").Trim();
        title = Regex.Replace(title, @"\s*(最新章节|全文阅读|免费阅读|全本小说网).*$", "").Trim();

        if (string.IsNullOrWhiteSpace(title)) title = bookId;

        // ── Author ────────────────────────────────────────────────────────────
        string author = "Unknown";
        var am = Regex.Match(cleanHtml,
            @"作者[：:]\s*<[^>]+>([^<]+)</",
            RegexOptions.IgnoreCase);
        if (!am.Success)
            am = Regex.Match(cleanHtml, @"作者[：:]\s*([^\s<\n,，]+)");
        if (am.Success) author = am.Groups[1].Value.Trim();

        // ── Chapter list ──────────────────────────────────────────────────────
        // AMP list page links: href="https://www.quanben.io/amp/n/{bookId}/{num}.html"
        // Regular list page links: href="/n/{bookId}/{num}.html"
        // We fetch the AMP list page (which has all chapters), so match both AMP and
        // non-AMP href styles. Chapter download URLs always use the non-AMP path.
        var chapterMatches = Regex.Matches(html,
            @"href=[""'](?:https?://(?:www\.)?quanben\.io)?/(?:amp/)?n/" + Regex.Escape(bookId) +
            @"/(\d+)\.html[""'][^>]*>(?:<span[^>]*>)?([^<]*)(?:</span>)?</a>",
            RegexOptions.IgnoreCase);

        var chapters = chapterMatches
            .Cast<Match>()
            .Select(m => new
            {
                Num   = int.Parse(m.Groups[1].Value),
                Title = System.Net.WebUtility.HtmlDecode(m.Groups[2].Value.Trim())
            })
            .DistinctBy(x => x.Num)
            .OrderBy(x => x.Num)
            .Select((x, i) => new ChapterRef(
                $"https://www.quanben.io/n/{bookId}/{x.Num}.html",
                string.IsNullOrWhiteSpace(x.Title) ? $"Chapter {i + 1}" : x.Title))
            .ToList();

        // ── Cover ─────────────────────────────────────────────────────────────
        string? cover = null;
        var ogM = Regex.Match(html,
            @"<meta[^>]+property=[""']og:image[""'][^>]+content=[""']([^""']+)[""']",
            RegexOptions.IgnoreCase);
        if (!ogM.Success)
            ogM = Regex.Match(html,
                @"<meta[^>]+content=[""']([^""']+)[""'][^>]+property=[""']og:image[""']",
                RegexOptions.IgnoreCase);
        if (ogM.Success) cover = ogM.Groups[1].Value.Trim();

        if (cover == null)
        {
            // Try any img tag that looks like a book cover
            var imgM = Regex.Match(html,
                @"<img[^>]+src=[""'](https?://[^""']*(?:cover|book|img)[^""']*)[""']",
                RegexOptions.IgnoreCase);
            if (imgM.Success) cover = imgM.Groups[1].Value.Trim();
        }

        return new IndexInfo(title, author, chapters, cover);
    }

    public List<string> ExtractChapterText(string html)
    {
        // Remove noise blocks
        html = Regex.Replace(html, @"<script[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<style[\s\S]*?</style>",   "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<nav[\s\S]*?</nav>",       "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<header[\s\S]*?</header>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<footer[\s\S]*?</footer>", "", RegexOptions.IgnoreCase);

        // quanben.io chapter content is in <div class="content"> or <div id="content">
        string? content = null;
        foreach (var pattern in new[]
        {
            @"<div[^>]+class=[""'][^""']*\bcontent\b[^""']*[""'][^>]*>([\s\S]+?)</div>\s*</div>",
            @"<div[^>]+class=[""'][^""']*\bcontent\b[^""']*[""'][^>]*>([\s\S]+)",
            @"<div[^>]+id=[""']content[""'][^>]*>([\s\S]+?)</div>\s*</div>",
            @"<div[^>]+id=[""']content[""'][^>]*>([\s\S]+)",
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

        content ??= html;

        // Convert <br> and <p> to newlines, strip remaining tags
        content = Regex.Replace(content, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        content = Regex.Replace(content, @"<p[^>]*>",  "\n", RegexOptions.IgnoreCase);
        content = Regex.Replace(content, @"<[^>]+>",   "");
        content = System.Net.WebUtility.HtmlDecode(content);

        var result = new List<string>();
        foreach (var line in content.Split('\n'))
        {
            string trimmed = line.Trim().TrimStart('\u3000').Trim();
            // Keep lines with CJK characters; skip watermarks/URLs
            if (trimmed.Length > 0 &&
                Regex.IsMatch(trimmed, @"[\u4e00-\u9fff\u3400-\u4dbf\uf900-\ufaff]") &&
                !trimmed.Contains("quanben.io") &&
                !trimmed.Contains("全本小说网") &&
                !Regex.IsMatch(trimmed, @"https?://"))
                result.Add(trimmed);
        }
        return result;
    }
}
