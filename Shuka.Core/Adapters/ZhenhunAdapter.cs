using System.Text.RegularExpressions;

namespace Shuka.Core.Adapters;

/// <summary>
/// Adapter for zhenhunxiaoshuo.com (镇魂小说网).
///
/// Index URL:   https://www.zhenhunxiaoshuo.com/{slug}/
/// Chapter URL: https://www.zhenhunxiaoshuo.com/{slug}/{n}.html
///
/// Multiple URL and container patterns are tried in order as fallbacks.
/// Set env var ZHENHUN_DEBUG=1 to dump raw HTML to a temp file for diagnosis.
/// </summary>
public class ZhenhunAdapter : ISiteAdapter
{
    public string SiteName => "zhenhunxiaoshuo.com";

    public bool Matches(string url) =>
        url.Contains("zhenhunxiaoshuo.com", StringComparison.OrdinalIgnoreCase);

    public string NormalizeUrl(string url)
    {
        if (url.StartsWith("http://")) url = "https://" + url[7..];
        if (!url.StartsWith("http"))   url = "https://" + url;

        // Strip chapter suffix: /slug/1.html → /slug/
        url = Regex.Replace(url, @"/\d+\.html$", "/");
        if (!url.EndsWith("/")) url += "/";
        return url;
    }

    public IndexInfo ParseIndex(string html, string indexUrl)
    {
        // ── Debug dump ───────────────────────────────────────────────────────
        // Set env var ZHENHUN_DEBUG=1 to dump the index HTML to a temp file.
        // Useful for diagnosing chapter-list parse failures on a live device.
        if (Environment.GetEnvironmentVariable("ZHENHUN_DEBUG") == "1")
        {
            try
            {
                string dumpPath = Path.Combine(Path.GetTempPath(), "zhenhun_index.html");
                File.WriteAllText(dumpPath, html, System.Text.Encoding.UTF8);
            }
            catch { /* ignore */ }
        }

        // ── Title ────────────────────────────────────────────────────────────
        string title = "";

        var titleM = Regex.Match(html, @"<h1[^>]*>\s*([^<]+?)\s*</h1>", RegexOptions.IgnoreCase);
        if (titleM.Success) title = titleM.Groups[1].Value.Trim();

        if (string.IsNullOrWhiteSpace(title))
        {
            foreach (var pat in new[]
            {
                @"<meta[^>]+property=[""']og:title[""'][^>]+content=[""']([^""']+)[""']",
                @"<meta[^>]+content=[""']([^""']+)[""'][^>]+property=[""']og:title[""']",
            })
            {
                var m = Regex.Match(html, pat, RegexOptions.IgnoreCase);
                if (m.Success) { title = m.Groups[1].Value.Trim(); break; }
            }
        }

        if (string.IsNullOrWhiteSpace(title))
            title = Regex.Match(html, @"<title[^>]*>([^<|_–\-<]+)", RegexOptions.IgnoreCase)
                         .Groups[1].Value.Trim();

        title = Regex.Replace(title, @"\s*(最新章节|无弹窗|全文阅读|免费阅读|在线阅读).*$", "").Trim();

        // ── Author ───────────────────────────────────────────────────────────
        string author = "Unknown";
        foreach (var pat in new[]
        {
            @"作者[：:]\s*<a[^>]*>([^<]+)</a>",
            @"作者[：:]\s*([^\s<\n,，【】&]+)",
            @"<span[^>]*class=[""'][^""']*author[^""']*[""'][^>]*>([^<]+)</span>",
        })
        {
            var m = Regex.Match(html, pat, RegexOptions.IgnoreCase);
            if (m.Success) { author = m.Groups[1].Value.Trim(); break; }
        }

        // ── Slug ─────────────────────────────────────────────────────────────
        var slugM = Regex.Match(indexUrl,
            @"zhenhunxiaoshuo\.com/([^/]+)/?$",
            RegexOptions.IgnoreCase);
        string slug = slugM.Success ? slugM.Groups[1].Value : "";

        // ── Chapter list — try several strategies in order ───────────────────
        var chapters = new List<ChapterRef>();

        // Strategy 1: links matching /slug/N.html (scoped, most reliable if slug known)
        if (!string.IsNullOrEmpty(slug) && chapters.Count == 0)
        {
            string pat = @"href=[""'](?:https?://(?:www\.)?zhenhunxiaoshuo\.com)?"
                       + "/" + Regex.Escape(slug) + @"/(\d+)\.html[""'][^>]*>([^<]*)</a>";

            chapters = Regex.Matches(html, pat, RegexOptions.IgnoreCase)
                .Cast<Match>()
                .Select(m => (Num: int.Parse(m.Groups[1].Value),
                              Title: System.Net.WebUtility.HtmlDecode(m.Groups[2].Value.Trim())))
                .DistinctBy(x => x.Num)
                .OrderBy(x => x.Num)
                .Select(x => new ChapterRef(
                    $"https://www.zhenhunxiaoshuo.com/{slug}/{x.Num}.html",
                    string.IsNullOrWhiteSpace(x.Title) ? $"Chapter {x.Num}" : x.Title))
                .ToList();
        }

        // Strategy 2: any /someSlug/N.html links on the page
        if (chapters.Count == 0)
        {
            string pat = @"href=[""'](?:https?://(?:www\.)?zhenhunxiaoshuo\.com)?/([a-z0-9\-]+)/(\d+)\.html[""'][^>]*>([^<]*)</a>";

            chapters = Regex.Matches(html, pat, RegexOptions.IgnoreCase)
                .Cast<Match>()
                .Select(m => (Slug2: m.Groups[1].Value,
                              Num:   int.Parse(m.Groups[2].Value),
                              Title: System.Net.WebUtility.HtmlDecode(m.Groups[3].Value.Trim())))
                .Where(x => string.IsNullOrEmpty(slug) || x.Slug2 == slug)
                .DistinctBy(x => x.Num)
                .OrderBy(x => x.Num)
                .Select(x => new ChapterRef(
                    $"https://www.zhenhunxiaoshuo.com/{x.Slug2}/{x.Num}.html",
                    string.IsNullOrWhiteSpace(x.Title) ? $"Chapter {x.Num}" : x.Title))
                .ToList();
        }

        // Strategy 3: look inside a chapter-list container and grab all <a> links
        if (chapters.Count == 0)
        {
            // Isolate the chapter list section to avoid nav links
            string? listSection = null;
            foreach (var containerPat in new[]
            {
                @"<(?:div|ul|ol)[^>]+id=[""'](?:chapterlist|chapter-list|catalog|mulu|list)[""'][^>]*>([\s\S]*?)</(?:div|ul|ol)>",
                @"<(?:div|ul|ol)[^>]+class=[""'][^""']*(?:chapterlist|chapter-list|catalog|mulu|booklist)[^""']*[""'][^>]*>([\s\S]*?)</(?:div|ul|ol)>",
            })
            {
                var cm = Regex.Match(html, containerPat, RegexOptions.IgnoreCase);
                if (cm.Success && cm.Groups[1].Value.Length > 20)
                {
                    listSection = cm.Groups[1].Value;
                    break;
                }
            }

            string scope = listSection ?? html;
            string aLinkPat = @"href=[""']([^""']+\.html)[""'][^>]*>([^<]+)</a>";

            chapters = Regex.Matches(scope, aLinkPat, RegexOptions.IgnoreCase)
                .Cast<Match>()
                .Select(m =>
                {
                    string href  = m.Groups[1].Value.Trim();
                    string label = System.Net.WebUtility.HtmlDecode(m.Groups[2].Value.Trim());

                    // Resolve relative href
                    if (!href.StartsWith("http"))
                        href = new Uri(new Uri(indexUrl), href).ToString();

                    // Only keep URLs that look like chapter pages
                    if (!href.Contains("zhenhunxiaoshuo.com")) return default;
                    if (!Regex.IsMatch(href, @"/\d+\.html$")) return default;

                    var numM = Regex.Match(href, @"/(\d+)\.html$");
                    int num = numM.Success ? int.Parse(numM.Groups[1].Value) : 0;
                    return (Num: num, Title: label, Url: href);
                })
                .Where(x => x != default && x.Num > 0)
                .DistinctBy(x => x.Num)
                .OrderBy(x => x.Num)
                .Select(x => new ChapterRef(x.Url, string.IsNullOrWhiteSpace(x.Title) ? $"Chapter {x.Num}" : x.Title))
                .ToList();
        }

        // Strategy 4: ultra-loose — any .html link containing the slug in the path
        if (chapters.Count == 0 && !string.IsNullOrEmpty(slug))
        {
            string pat = @"href=[""']([^""']*/" + Regex.Escape(slug) + @"/[^""']+)[""'][^>]*>([^<]*)</a>";

            chapters = Regex.Matches(html, pat, RegexOptions.IgnoreCase)
                .Cast<Match>()
                .Select(m =>
                {
                    string href  = m.Groups[1].Value.Trim();
                    string label = System.Net.WebUtility.HtmlDecode(m.Groups[2].Value.Trim());
                    if (!href.StartsWith("http"))
                        href = new Uri(new Uri(indexUrl), href).ToString();
                    var numM = Regex.Match(href, @"/(\d+)\.html$");
                    if (!numM.Success) return default;
                    int num = int.Parse(numM.Groups[1].Value);
                    return (Num: num, Title: label, Url: href);
                })
                .Where(x => x != default && x.Num > 0)
                .DistinctBy(x => x.Num)
                .OrderBy(x => x.Num)
                .Select(x => new ChapterRef(x.Url, string.IsNullOrWhiteSpace(x.Title) ? $"Chapter {x.Num}" : x.Title))
                .ToList();
        }

        // ── Cover ─────────────────────────────────────────────────────────────
        string? cover = null;
        foreach (var pat in new[]
        {
            @"<meta[^>]+property=[""']og:image[""'][^>]+content=[""']([^""']+)[""']",
            @"<meta[^>]+content=[""']([^""']+)[""'][^>]+property=[""']og:image[""']",
            @"<div[^>]+class=[""'][^""']*(?:book-img|cover|thumb)[^""']*[""'][^>]*>[\s\S]*?<img[^>]+src=[""']([^""']+)[""']",
            @"<img[^>]+src=[""']([^""']*(?:cover|thumb|book)[^""']*)[""']",
        })
        {
            var m = Regex.Match(html, pat, RegexOptions.IgnoreCase);
            if (m.Success) { cover = m.Groups[1].Value.Trim(); break; }
        }

        if (cover != null && !cover.StartsWith("http"))
            cover = new Uri(new Uri(indexUrl), cover).ToString();

        return new IndexInfo(title, author, chapters, cover);
    }

    public List<string> ExtractChapterText(string html)
    {
        // ── Debug dump ───────────────────────────────────────────────────────
        if (Environment.GetEnvironmentVariable("ZHENHUN_DEBUG") == "1")
        {
            try
            {
                string dumpPath = Path.Combine(Path.GetTempPath(), "zhenhun_chapter.html");
                File.WriteAllText(dumpPath, html, System.Text.Encoding.UTF8);
            }
            catch { /* ignore */ }
        }

        html = Regex.Replace(html, @"<script[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<style[\s\S]*?</style>",   "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<nav[\s\S]*?</nav>",       "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<header[\s\S]*?</header>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<footer[\s\S]*?</footer>", "", RegexOptions.IgnoreCase);

        // Try content containers from most specific to least
        string? fragment = null;
        foreach (var pattern in new[]
        {
            // id-based
            @"<div[^>]+id=[""']chaptercontent[""'][^>]*>([\s\S]+?)</div>\s*<div",
            @"<div[^>]+id=[""']content[""'][^>]*>([\s\S]+?)</div>\s*<div",
            @"<div[^>]+id=[""']nr1[""'][^>]*>([\s\S]+?)</div>",
            @"<div[^>]+id=[""']booktxt[""'][^>]*>([\s\S]+?)</div>",
            @"<div[^>]+id=[""']readcontent[""'][^>]*>([\s\S]+?)</div>",
            // class-based
            @"<div[^>]+class=[""'][^""']*\bchapter[_-]?content\b[^""']*[""'][^>]*>([\s\S]+?)</div>\s*<div",
            @"<div[^>]+class=[""'][^""']*\bread[_-]?content\b[^""']*[""'][^>]*>([\s\S]+?)</div>\s*<div",
            @"<div[^>]+class=[""'][^""']*\bcontent\b[^""']*[""'][^>]*>([\s\S]+?)</div>\s*<div",
            @"<article[^>]*>([\s\S]+?)</article>",
        })
        {
            var m = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
            if (m.Success && m.Groups[1].Value.Length > 100)
            {
                fragment = m.Groups[1].Value;
                break;
            }
        }

        fragment ??= html;

        // Normalise breaks/paragraphs → newlines then strip tags
        fragment = Regex.Replace(fragment, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        fragment = Regex.Replace(fragment, @"<p[^>]*>",  "\n", RegexOptions.IgnoreCase);
        fragment = Regex.Replace(fragment, @"<[^>]+>",   "");
        string text = System.Net.WebUtility.HtmlDecode(fragment);

        var result = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            string trimmed = line.Trim().TrimStart('\u3000').Trim();
            if (trimmed.Length == 0) continue;
            if (IsNoiseLine(trimmed)) continue;
            if (Regex.IsMatch(trimmed, @"[\u4e00-\u9fff\u3400-\u4dbf\uf900-\ufaff]"))
                result.Add(trimmed);
        }

        return result;
    }

    private static bool IsNoiseLine(string t) =>
        t.Contains("zhenhunxiaoshuo", StringComparison.OrdinalIgnoreCase) ||
        t.Contains("镇魂小说网", StringComparison.Ordinal) ||
        t.Contains("www.", StringComparison.OrdinalIgnoreCase) ||
        t.Contains("http",  StringComparison.OrdinalIgnoreCase) ||
        Regex.IsMatch(t, @"(上一章|下一章|返回目录|章节目录|目录|推荐阅读|书签|收藏|加入书架|全文阅读)");
}
