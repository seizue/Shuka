using System.Text.RegularExpressions;

namespace Shuka.Core.Adapters;

/// <summary>
/// Adapter for m.fushuw.org (福书网) — Simplified Chinese novel site.
///
/// Index URL format:   https://m.fushuw.org/{category}/{numericId}.html
///   e.g. https://m.fushuw.org/chuanyuechongsheng/41341.html
///
/// Chapter URL format: https://m.fushuw.org/{category}/{id}_{page}.html
///   e.g. https://m.fushuw.org/chuanyuechongsheng/41341_2.html
///
/// ECMS-based site, same CMS as dmxs.org.
/// No Cloudflare, no paywall, UTF-8 encoding.
/// </summary>
public class FushuwAdapter : ISiteAdapter
{
    public string SiteName => "fushuw.org";

    public bool RequiresCfBypass => false;

    public bool Matches(string url) =>
        url.Contains("fushuw.org", StringComparison.OrdinalIgnoreCase);

    public string NormalizeUrl(string url)
    {
        if (!url.StartsWith("http")) url = "https://" + url;

        // Normalise to mobile subdomain — all fetches use m.fushuw.org
        url = Regex.Replace(url,
            @"https?://(?:www\.)?fushuw\.org/",
            "https://m.fushuw.org/",
            RegexOptions.IgnoreCase);

        // If this is a chapter URL /{category}/{id}_{page}.html, strip the _{page} suffix
        // to get the canonical index URL /{category}/{id}.html
        // (_0 and .html are equivalent: both serve page 0)
        var chapterM = Regex.Match(url,
            @"(https?://m\.fushuw\.org/[a-zA-Z0-9]+/\d+)_\d+\.html",
            RegexOptions.IgnoreCase);
        if (chapterM.Success)
            return chapterM.Groups[1].Value + ".html";

        // Strip query/fragment from index URL
        var indexM = Regex.Match(url,
            @"(https?://m\.fushuw\.org/[a-zA-Z0-9]+/\d+\.html)",
            RegexOptions.IgnoreCase);
        return indexM.Success ? indexM.Groups[1].Value : url;
    }

    public IndexInfo ParseIndex(string html, string indexUrl)
    {
        // Extract the numeric book ID from the index URL
        string bookId = Regex.Match(indexUrl, @"/([0-9]+)\.html$").Groups[1].Value;

        // ── Title ─────────────────────────────────────────────────────────────
        string title = Regex.Match(html, @"<h1[^>]*>\s*([^<]+?)\s*</h1>",
            RegexOptions.IgnoreCase).Groups[1].Value.Trim();
        if (string.IsNullOrWhiteSpace(title))
            title = Regex.Match(html, @"<title[^>]*>([^<|_–\-]+)",
                RegexOptions.IgnoreCase).Groups[1].Value.Trim();

        // ── Author ────────────────────────────────────────────────────────────
        string author = "Unknown";
        var am = Regex.Match(html,
            @"作者[：:]\s*<a[^>]*>([^<]+)</a>",
            RegexOptions.IgnoreCase);
        if (!am.Success)
            am = Regex.Match(html, @"作者[：:]\s*([^\s<\n,，]+)");
        if (am.Success) author = am.Groups[1].Value.Trim();

        // ── Chapter list ──────────────────────────────────────────────────────
        // The site has no TOC. Extract total chapter count from pagination text
        // e.g. "1058   上一页 1 下一页 尾页"
        // Extract category and id from the index URL.
        var urlM = Regex.Match(indexUrl,
            @"(?:m\.|www\.)?fushuw\.org/([a-zA-Z0-9]+)/(\d+)\.html",
            RegexOptions.IgnoreCase);
        string category = urlM.Success ? urlM.Groups[1].Value : "";
        string numericId = urlM.Success ? urlM.Groups[2].Value : bookId;

        int totalChapters = 1;
        // Pagination is: <b>204</b> ... 上一页 ... 下一页 ... <a href="/{cat}/{id}_{last}.html">尾页</a>
        // Most reliable: extract the last page number from the 尾页 href.
        // e.g. href="/wucpxiang/58365_203.html" → last page = 203 → total = 204
        var tailM = Regex.Match(html,
            @"href=[""']/[a-zA-Z0-9]+/\d+_(\d+)\.html[""'][^>]*>尾页",
            RegexOptions.IgnoreCase);
        if (tailM.Success && int.TryParse(tailM.Groups[1].Value, out int lastPage))
        {
            // Pages go from _0 to _lastPage inclusive
            totalChapters = lastPage + 1;
        }
        else
        {
            // Fallback: bold number inside pageLink paragraph — <b>204</b>
            var boldM = Regex.Match(html,
                @"class=[""']pageLink[""'][^>]*>[\s\S]*?<[^>]*>\s*<b>(\d+)</b>",
                RegexOptions.IgnoreCase);
            if (!boldM.Success)
                boldM = Regex.Match(html, @"title=[""']总数[""'][^>]*>\s*&nbsp;<b>(\d+)</b>",
                    RegexOptions.IgnoreCase);
            if (boldM.Success && int.TryParse(boldM.Groups[1].Value, out int t))
                totalChapters = t;
        }

        List<ChapterRef> chapters;

        // Best source: <option> tags in the titleselect dropdown — present in static HTML.
        // e.g. <option value="/wucpxiang/58365_0.html" selected>书名</option>
        //      <option value="/wucpxiang/58365_1.html">书名(2)</option>
        var optionMatches = Regex.Matches(html,
            @"<option[^>]+value=[""'](/[a-zA-Z0-9]+/\d+_\d+\.html)[""'][^>]*>([^<]*)</option>",
            RegexOptions.IgnoreCase);

        if (optionMatches.Count > 0)
        {
            chapters = optionMatches
                .Cast<Match>()
                .Select((m, i) => new ChapterRef(
                    "https://m.fushuw.org" + m.Groups[1].Value,
                    $"第{i + 1}章"))
                .ToList();
            // Update totalChapters from options count if extraction succeeded
            if (totalChapters == 1) totalChapters = chapters.Count;
        }
        else if (!string.IsNullOrEmpty(numericId) && !string.IsNullOrEmpty(category))
        {
            // Pages are numbered from 0: {id}_0.html, {id}_1.html, ... {id}_{N-1}.html
            chapters = Enumerable.Range(0, totalChapters)
                .Select(n => new ChapterRef(
                    $"https://m.fushuw.org/{category}/{numericId}_{n}.html",
                    $"第{n + 1}章"))
                .ToList();
        }
        else
        {
            chapters = new List<ChapterRef>();
        }

        // ── Cover ─────────────────────────────────────────────────────────────
        // Cover is in: <img class="j-lazy" src="http://www.fushuw.org/d/file/p/...jpg">
        string? cover = null;

        // Primary: fushuw cover pattern — d/file/p/...
        var coverM = Regex.Match(html,
            @"<img[^>]+src=[""'](https?://[^""']*fushuw\.org/d/file/[^""']+\.(?:jpg|jpeg|png|webp))[""']",
            RegexOptions.IgnoreCase);
        if (!coverM.Success)
            coverM = Regex.Match(html,
                @"data-original=[""'](https?://[^""']*fushuw\.org/d/file/[^""']+\.(?:jpg|jpeg|png|webp))[""']",
                RegexOptions.IgnoreCase);
        if (coverM.Success)
        {
            cover = coverM.Groups[1].Value.Trim();
            // Upgrade http → https
            if (cover.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                cover = "https://" + cover[7..];
        }

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
            if (ogM.Success) cover = ogM.Groups[1].Value.Trim();
        }

        // Make cover URL absolute if relative
        if (cover != null && !cover.StartsWith("http"))
            cover = "https://m.fushuw.org" + (cover.StartsWith("/") ? cover : "/" + cover);

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

        // Try ECMS content containers in order of specificity
        string? content = TryExtractContent(html);

        content ??= html;

        var result = new List<string>();
        foreach (var para in SplitParagraphs(content))
        {
            string trimmed = para.Trim().TrimStart('\u3000').Trim();
            if (trimmed.Length == 0 || IsFushuwNoiseLine(trimmed))
                continue;
            // Keep lines containing CJK characters
            if (Regex.IsMatch(trimmed, @"[\u4e00-\u9fff\u3400-\u4dbf\uf900-\ufaff]"))
                result.Add(trimmed);
        }

        // On page 1 the description block (title, author, synopsis, tags) precedes
        // the chapter text. Drop everything before the first "第N章" heading.
        int chapterStart = result.FindIndex(l => Regex.IsMatch(l, @"^第\d+章"));
        if (chapterStart > 0)
            result.RemoveRange(0, chapterStart);

        return result;
    }

    /// <summary>
    /// Tries to isolate the chapter prose from known ECMS content containers.
    /// Checks read_chapterDetail (dmxs-style), then id=content, class=content,
    /// class=chapter-content, and &lt;article&gt;.
    /// </summary>
    private static string? TryExtractContent(string html)
    {
        // fushuw.org: content is in <td id="text" ...>
        // Using a broad match since <td> content may span multiple nested tags.
        var idText = Regex.Match(html,
            @"<td[^>]+id=[""']text[""'][^>]*>([\s\S]+)",
            RegexOptions.IgnoreCase);
        if (idText.Success && idText.Groups[1].Length > 100)
        {
            // Trim at the closing </td> if present, otherwise take all
            string chunk = idText.Groups[1].Value;
            int tdClose = chunk.IndexOf("</td>", StringComparison.OrdinalIgnoreCase);
            if (tdClose > 100) chunk = chunk[..tdClose];
            return chunk;
        }

        // dmxs/ECMS: div.read_chapterDetail
        const string readDetailOpen = @"<div[^>]+class\s*=\s*[""'][^""']*\bread_chapterDetail\b[^""']*[""'][^>]*>";
        var anchored = Regex.Match(html,
            readDetailOpen + @"([\s\S]*?)</div>\s*<div[^>]+class\s*=\s*[""'][^""']*\b(?:pageNav|newpageNav)\b",
            RegexOptions.IgnoreCase);
        if (anchored.Success && anchored.Groups[1].Length > 20)
            return anchored.Groups[1].Value;

        var loose = Regex.Match(html, readDetailOpen + @"([\s\S]*?)</div>", RegexOptions.IgnoreCase);
        if (loose.Success && loose.Groups[1].Length > 20)
            return loose.Groups[1].Value;

        // id="content"
        var idContent = Regex.Match(html,
            @"<div[^>]+id=[""']content[""'][^>]*>([\s\S]*?)</div>",
            RegexOptions.IgnoreCase);
        if (idContent.Success && idContent.Groups[1].Length > 100)
            return idContent.Groups[1].Value;

        // class="content" or class="chapter-content"
        foreach (var pattern in new[]
        {
            @"<div[^>]+class=[""'][^""']*\bcontent\b[^""']*[""'][^>]*>([\s\S]*?)</div>",
            @"<div[^>]+class=[""'][^""']*\bchapter-content\b[^""']*[""'][^>]*>([\s\S]*?)</div>",
            @"<article[^>]*>([\s\S]*?)</article>",
        })
        {
            var m = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
            if (m.Success && m.Groups[1].Value.Length > 100)
                return m.Groups[1].Value;
        }

        return null;
    }

    /// <summary>
    /// Splits an HTML fragment into plain-text lines via &lt;p&gt; tags,
    /// falling back to &lt;br&gt; and tag-stripping when no paragraphs are found.
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
    /// Drops navigation and UI strings that appear inside the content area.
    /// </summary>
    private static bool IsFushuwNoiseLine(string t)
    {
        string withoutRepl = t.Replace("\uFFFD", "", StringComparison.Ordinal).Trim();
        if (withoutRepl.Length == 0)
            return true;

        if (Regex.IsMatch(t,
                @"(返回目录|下一章|尾章|回目录|章节目录|推荐阅读|上一章|下一页|上一页)",
                RegexOptions.IgnoreCase))
            return true;

        if (Regex.IsMatch(t, @"^\d+/\d+$"))
            return true;

        // Site watermark lines (e.g. www.fushuw.org, m.fushuw.org, www[.]fushuw[.]org)
        if (Regex.IsMatch(t, @"fushuw", RegexOptions.IgnoreCase))
            return true;

        // Donation / server cost text
        if (Regex.IsMatch(t, @"自愿捐助|捐助|服务器"))
            return true;

        return false;
    }
}
