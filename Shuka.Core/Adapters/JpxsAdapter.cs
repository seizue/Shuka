using System.Text.RegularExpressions;

namespace Shuka.Core.Adapters;

/// <summary>
/// Adapter for jpxs123.com (精品小說網) — Traditional Chinese novel site.
///
/// Index URL format:   https://jpxs123.com/{category}/{numericId}.html
///   e.g. https://jpxs123.com/cyjk/11624.html
///
/// Chapter URL format: https://jpxs123.com/{category}/{numericId}/{chapterNum}.html
///   e.g. https://jpxs123.com/cyjk/11624/1.html
///
/// ECMS-based site, same CMS family as dmxs.org. GBK encoding — handled
/// automatically by HttpFetcher's charset auto-detection.
/// No Cloudflare, no paywall.
/// </summary>
public class JpxsAdapter : ISiteAdapter
{
    public string SiteName => "jpxs123.com";

    public bool RequiresCfBypass => false;

    public bool Matches(string url) =>
        url.Contains("jpxs123.com", StringComparison.OrdinalIgnoreCase);

    public string NormalizeUrl(string url)
    {
        if (!url.StartsWith("http")) url = "https://" + url;

        // Normalize www.jpxs123.com → jpxs123.com
        url = Regex.Replace(url,
            @"https?://www\.jpxs123\.com/",
            "https://jpxs123.com/",
            RegexOptions.IgnoreCase);

        // If this is a chapter URL /{category}/{id}/{n}.html, strip the /{n}.html
        // part to get the canonical index URL /{category}/{id}.html
        var chapterM = Regex.Match(url,
            @"(https?://jpxs123\.com/[a-zA-Z0-9]+/\d+)/\d+\.html",
            RegexOptions.IgnoreCase);
        if (chapterM.Success)
            return chapterM.Groups[1].Value + ".html";

        // Strip query/fragment from index URL
        var indexM = Regex.Match(url,
            @"(https?://jpxs123\.com/[a-zA-Z0-9]+/\d+\.html)",
            RegexOptions.IgnoreCase);
        return indexM.Success ? indexM.Groups[1].Value : url;
    }

    public IndexInfo ParseIndex(string html, string indexUrl)
    {
        // ── Title ─────────────────────────────────────────────────────────────
        // <h1>全球迷宮：開局設計精絕古城(1-367)</h1> — strip the (1-N) chapter-count suffix
        string title = Regex.Match(html, @"<h1[^>]*>\s*([^<]+?)\s*</h1>",
            RegexOptions.IgnoreCase).Groups[1].Value.Trim();
        if (string.IsNullOrWhiteSpace(title))
            title = Regex.Match(html, @"<title[^>]*>([^<|_–\-]+)",
                RegexOptions.IgnoreCase).Groups[1].Value.Trim();
        title = System.Net.WebUtility.HtmlDecode(title);
        title = Regex.Replace(title, @"\s*[\(（]\s*\d+\s*[-–]\s*\d+\s*[\)）]\s*$", "").Trim();

        // ── Author ────────────────────────────────────────────────────────────
        // Pattern: <span>作者：<a href=...>無戒僧</a></span>
        string author = "Unknown";
        var am = Regex.Match(html,
            @"作者[：:]\s*<a[^>]*>([^<]+)</a>",
            RegexOptions.IgnoreCase);
        if (!am.Success)
            am = Regex.Match(html, @"作者[：:]\s*([^\s<\n,，]+)");
        if (am.Success) author = am.Groups[1].Value.Trim();

        // ── Chapter list ──────────────────────────────────────────────────────
        // Links look like: href="/cyjk/11624/1.html"  → 第1节
        // Restrict to the book_list container so nav/related-book links are not picked up.
        string listHtml = Regex.Match(html,
                @"<div[^>]+class=[""'][^""']*book_list[^""']*[""'][^>]*>([\s\S]*?)</div>",
                RegexOptions.IgnoreCase)
            .Groups[1].Value;
        if (string.IsNullOrWhiteSpace(listHtml))
            listHtml = html;

        // Extract category and numeric id from the index URL
        var urlM = Regex.Match(indexUrl,
            @"jpxs123\.com/([a-zA-Z0-9]+)/(\d+)\.html",
            RegexOptions.IgnoreCase);
        string category = urlM.Success ? urlM.Groups[1].Value : "";
        string numericId = urlM.Success ? urlM.Groups[2].Value : "";

        var chapterMatches = Regex.Matches(listHtml,
            @"href=[""'](?:https?://(?:www\.)?jpxs123\.com)?/([a-zA-Z0-9]+)/(\d+)/(\d+)\.html[""'][^>]*>([^<]*)</a>",
            RegexOptions.IgnoreCase);

        var chapters = chapterMatches
            .Cast<Match>()
            .Where(m => m.Groups[2].Value == numericId) // only this book's chapters
            .Select(m => new
            {
                Num   = int.Parse(m.Groups[3].Value),
                Cat   = m.Groups[1].Value,
                Id    = m.Groups[2].Value,
                Title = System.Net.WebUtility.HtmlDecode(m.Groups[4].Value.Trim())
            })
            .DistinctBy(x => x.Num)
            .OrderBy(x => x.Num)
            .Select(x => new ChapterRef(
                $"https://jpxs123.com/{x.Cat}/{x.Id}/{x.Num}.html",
                string.IsNullOrWhiteSpace(x.Title) ? $"第{x.Num}节" : x.Title))
            .ToList();

        // ── Cover ─────────────────────────────────────────────────────────────
        // <div class="pic"><img src="/d/file/p/20250922/xxx.jpg" alt="书名"></div>
        string? cover = null;
        var coverM = Regex.Match(html,
            @"<img[^>]+src=[""'](/d/file/p/[^""']+\.(?:jpg|jpeg|png|webp|gif))[""']",
            RegexOptions.IgnoreCase);
        if (coverM.Success)
            cover = "https://jpxs123.com" + coverM.Groups[1].Value;

        if (cover == null)
        {
            // Fallback: og:image
            var ogM = Regex.Match(html,
                @"<meta[^>]+property=[""']og:image[""'][^>]+content=[""']([^""']+)[""']",
                RegexOptions.IgnoreCase);
            if (!ogM.Success)
                ogM = Regex.Match(html,
                    @"<meta[^>]+content=[""']([^""']+)[""'][^>]+property=[""']og:image[""']",
                    RegexOptions.IgnoreCase);
            if (ogM.Success)
            {
                cover = ogM.Groups[1].Value.Trim();
                if (cover.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                    cover = "https://" + cover[7..];
                if (cover.StartsWith("/"))
                    cover = "https://jpxs123.com" + cover;
            }
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

        // Content lives in <div class="read_chapterDetail"> — same ECMS layout as dmxs.org
        string? content = TryExtractReadChapterDetail(html);
        if (content == null)
        {
            foreach (var pattern in new[]
            {
                @"<div[^>]+id=[""']content[""'][^>]*>([\s\S]*?)</div>",
                @"<div[^>]+class=[""'][^""']*\bcontent\b[^""']*[""'][^>]*>([\s\S]*?)</div>",
                @"<article[^>]*>([\s\S]*?)</article>",
            })
            {
                var m = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
                if (m.Success && m.Groups[1].Value.Length > 100)
                {
                    content = m.Groups[1].Value;
                    break;
                }
            }
        }

        content ??= html;

        var result = new List<string>();
        foreach (var para in SplitParagraphs(content))
        {
            string trimmed = para.Trim().TrimStart('\u3000').Trim();
            if (trimmed.Length == 0 || IsJpxsNoiseLine(trimmed))
                continue;
            // Novel body: keep CJK lines
            if (Regex.IsMatch(trimmed, @"[\u4e00-\u9fff\u3400-\u4dbf\uf900-\ufaff]"))
                result.Add(trimmed);
        }

        // Chapter 1 opens with the book description (title, author, synopsis)
        // before the actual story. Drop everything before the first chapter
        // heading (第1节 / 第一章 …) when one is present.
        int chapterStart = result.FindIndex(l =>
            Regex.IsMatch(l, @"^第\s*[0-9〇零一二三四五六七八九十百千]+\s*[节節章卷回]"));
        if (chapterStart > 0)
            result.RemoveRange(0, chapterStart);
        else
        {
            // No explicit heading: drop leading metadata lines (author/date/tag lines)
            while (result.Count > 0 && IsDescriptionHeaderLine(result[0]))
                result.RemoveAt(0);
        }

        return result;
    }

    /// <summary>
    /// Isolates <c>div.read_chapterDetail</c> inner HTML, falling back to a loose
    /// first-div close when the trailing page-nav anchor is absent.
    /// </summary>
    private static string? TryExtractReadChapterDetail(string html)
    {
        const string open = @"<div[^>]+class\s*=\s*[""'][^""']*\bread_chapterDetail\b[^""']*[""'][^>]*>";

        var anchored = Regex.Match(html,
            open + @"([\s\S]*?)</div>\s*<div[^>]+class\s*=\s*[""'][^""']*\b(?:pageNav|newpageNav|page)\b",
            RegexOptions.IgnoreCase);
        if (anchored.Success && anchored.Groups[1].Length > 20)
            return anchored.Groups[1].Value;

        var loose = Regex.Match(html, open + @"([\s\S]*?)</div>", RegexOptions.IgnoreCase);
        if (loose.Success && loose.Groups[1].Length > 20)
            return loose.Groups[1].Value;

        return null;
    }

    /// <summary>
    /// Pull plain text from <c>&lt;p&gt;</c> blocks; fall back to br/tag stripping.
    /// </summary>
    private static IEnumerable<string> SplitParagraphs(string htmlFragment)
    {
        var matches = Regex.Matches(htmlFragment, @"<p[^>]*>([\s\S]*?)</p>", RegexOptions.IgnoreCase);
        if (matches.Count > 0)
        {
            foreach (Match m in matches)
            {
                string chunk = m.Groups[1].Value;
                chunk = Regex.Replace(chunk, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
                chunk = Regex.Replace(chunk, @"<[^>]+>", "");
                chunk = System.Net.WebUtility.HtmlDecode(chunk);
                foreach (var line in chunk.Split('\n'))
                {
                    var t = line.Trim();
                    if (t.Length > 0)
                        yield return t;
                }
            }
            yield break;
        }

        string legacy = Regex.Replace(htmlFragment, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        legacy = Regex.Replace(legacy, @"<p[^>]*>", "\n", RegexOptions.IgnoreCase);
        legacy = Regex.Replace(legacy, @"<[^>]+>", "");
        legacy = System.Net.WebUtility.HtmlDecode(legacy);
        foreach (var line in legacy.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length > 0)
                yield return t;
        }
    }

    /// <summary>
    /// Drops navigation / UI / site-watermark strings from the chapter body.
    /// </summary>
    private static bool IsJpxsNoiseLine(string t)
    {
        string withoutRepl = t.Replace("\uFFFD", "", StringComparison.Ordinal).Trim();
        if (withoutRepl.Length == 0)
            return true;

        if (Regex.IsMatch(t,
                @"(返回目[录錄]|下一[章节節]|上一[章节節]|尾[页頁章]|回目[录錄]|章节目录|章節目錄|推荐阅读|推薦閱讀)",
                RegexOptions.IgnoreCase))
            return true;

        if (Regex.IsMatch(t, @"^\d+/\d+$"))
            return true;

        // Site watermark
        if (Regex.IsMatch(t, @"jpxs123", RegexOptions.IgnoreCase))
            return true;
        if (Regex.IsMatch(t, @"精品小[说說]"))
            return true;

        return false;
    }

    /// <summary>
    /// Recognizes the metadata lines that precede the story text on page 1
    /// (title line, 作者：/時間： lines, category/tag/status lines).
    /// </summary>
    private static bool IsDescriptionHeaderLine(string t)
    {
        if (Regex.IsMatch(t, @"^(作者|時間|时间|分類|分类|狀態|状态|字[數数]|更新)[：:]"))
            return true;
        if (Regex.IsMatch(t, @"^[\(（\[【].*[\)）\]】]$") && t.Length < 60)
            return true;
        // Chapter-count suffix like "書名(1-367)" on its own line
        if (Regex.IsMatch(t, @"\(\s*\d+\s*[-–]\s*\d+\s*\)$") && t.Length < 60)
            return true;
        return false;
    }
}
