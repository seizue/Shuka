using System.Text.RegularExpressions;

namespace Shuka.Core.Adapters;

/// <summary>
/// Adapter for dmxs.org (耽美/GL Chinese novel site).
///
/// Index URL formats accepted:
///   https://www.dmxs.org/{category}/{bookId}.html
///   e.g. https://www.dmxs.org/GLBH/1840.html
///
/// Chapter URL format:
///   https://www.dmxs.org/view/{classId}-{bookId}-{chapterNum}.html
///   e.g. https://www.dmxs.org/view/11-1840-1.html
///
/// The site uses GBK encoding — handled automatically by HttpFetcher's
/// charset auto-detection from the HTML meta tag.
/// No Cloudflare, no paywall, no obfuscation.
/// </summary>
public class DmxsAdapter : ISiteAdapter
{
    public string SiteName => "dmxs.org";

    public bool Matches(string url) =>
        url.Contains("dmxs.org", StringComparison.OrdinalIgnoreCase);

    public string NormalizeUrl(string url)
    {
        if (!url.StartsWith("http")) url = "https://" + url;

        // If user pastes a chapter URL, redirect to the index page.
        // Chapter: /view/{classId}-{bookId}-{num}.html
        var chapterM = Regex.Match(url,
            @"https?://(?:www\.)?dmxs\.org/view/(\d+)-(\d+)-\d+\.html",
            RegexOptions.IgnoreCase);
        if (chapterM.Success)
        {
            // We need the category slug — fall back to a generic lookup via the
            // chapter page's breadcrumb. For now just return the URL as-is and
            // let ParseIndex handle it if it's already an index page.
            // The user should paste the index URL, but we handle this gracefully.
            return url;
        }

        // Strip query/fragment from index URL
        var indexM = Regex.Match(url,
            @"(https?://(?:www\.)?dmxs\.org/[^/?#]+/\d+\.html)",
            RegexOptions.IgnoreCase);
        return indexM.Success ? indexM.Groups[1].Value : url;
    }

    public IndexInfo ParseIndex(string html, string indexUrl)
    {
        // ── Title ─────────────────────────────────────────────────────────────
        // <h1> or <title> before the first pipe/dash
        string title = Regex.Match(html, @"<h1[^>]*>\s*([^<]+?)\s*</h1>",
            RegexOptions.IgnoreCase).Groups[1].Value.Trim();
        if (string.IsNullOrWhiteSpace(title))
            title = Regex.Match(html, @"<title[^>]*>([^<|_–-]+)",
                RegexOptions.IgnoreCase).Groups[1].Value.Trim();

        // ── Author ────────────────────────────────────────────────────────────
        // Pattern: 作者：<a ...>name</a>  or  作者：name
        string author = "Unknown";
        var am = Regex.Match(html,
            @"作者[：:]\s*<a[^>]*>([^<]+)</a>",
            RegexOptions.IgnoreCase);
        if (!am.Success)
            am = Regex.Match(html, @"作者[：:]\s*([^\s<\n,，]+)");
        if (am.Success) author = am.Groups[1].Value.Trim();

        // ── Chapter list ──────────────────────────────────────────────────────
        // Links look like: href="https://www.dmxs.org/view/11-1840-5.html"
        // or relative:     href="/view/11-1840-5.html"
        var chapterMatches = Regex.Matches(html,
            @"href=[""'](?:https?://(?:www\.)?dmxs\.org)?/view/(\d+)-(\d+)-(\d+)\.html[""'][^>]*>([^<]*)</a>",
            RegexOptions.IgnoreCase);

        var chapters = chapterMatches
            .Cast<Match>()
            .Select(m => new
            {
                ClassId = m.Groups[1].Value,
                BookId  = m.Groups[2].Value,
                Num     = int.Parse(m.Groups[3].Value),
                Title   = System.Net.WebUtility.HtmlDecode(m.Groups[4].Value.Trim()),
                Url     = $"https://www.dmxs.org/view/{m.Groups[1].Value}-{m.Groups[2].Value}-{m.Groups[3].Value}.html"
            })
            .DistinctBy(x => x.Num)
            .OrderBy(x => x.Num)
            .Select(x => new ChapterRef(x.Url,
                string.IsNullOrWhiteSpace(x.Title) ? $"Chapter {x.Num}" : x.Title))
            .ToList();

        // ── Cover ─────────────────────────────────────────────────────────────
        // dmxs.org doesn't typically have cover images on the index page,
        // but check og:image and any img with the book ID in the src just in case.
        string? cover = null;
        var ogM = Regex.Match(html,
            @"<meta[^>]+property=[""']og:image[""'][^>]+content=[""']([^""']+)[""']",
            RegexOptions.IgnoreCase);
        if (!ogM.Success)
            ogM = Regex.Match(html,
                @"<meta[^>]+content=[""']([^""']+)[""'][^>]+property=[""']og:image[""']",
                RegexOptions.IgnoreCase);
        if (ogM.Success) cover = ogM.Groups[1].Value.Trim();

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

        // dmxs chapter content is in <div id="content"> or <div class="content">
        string? content = null;
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
            // Keep lines with CJK characters or meaningful punctuation-only lines
            if (trimmed.Length > 0 &&
                Regex.IsMatch(trimmed, @"[\u4e00-\u9fff\u3400-\u4dbf\uf900-\ufaff]"))
                result.Add(trimmed);
        }
        return result;
    }
}
