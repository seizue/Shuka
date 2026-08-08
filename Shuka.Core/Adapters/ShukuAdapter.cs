using System.Net;
using System.Text.RegularExpressions;

namespace Shuka.Core.Adapters;

// 52shuku.net adapter
public class ShukuAdapter : ISiteAdapter
{
    public string SiteName => "52shuku.net";

    public bool Matches(string url) =>
        url.Contains("52shuku.net", StringComparison.OrdinalIgnoreCase);

    public string NormalizeUrl(string url)
    {
        // Strip chapter suffix: bkd7d_2.html -> bkd7d.html
        url = Regex.Replace(url, @"_\d+\.html$", ".html");
        if (url.StartsWith("http://")) url = "https://" + url[7..];
        if (!url.StartsWith("http")) url = "https://" + url;
        return url;
    }

    public IndexInfo ParseIndex(string html, string indexUrl)
    {
        string title = Regex.Match(html, @"<h1[^>]*>([\s\S]*?)</h1>", RegexOptions.IgnoreCase).Groups[1].Value;
        title = Regex.Replace(title, @"<[^>]+>", "").Trim();
        title = Regex.Replace(title, @"\s*\(\d+\)\s*$", "").Trim();
        if (string.IsNullOrWhiteSpace(title))
            title = Regex.Match(indexUrl, @"/([^/]+)\.html$").Groups[1].Value;

        string author = "Unknown";
        var am = Regex.Match(html, @"作者[：:]\s*([^\s【\n】<&]+)");
        if (am.Success) author = am.Groups[1].Value.Trim();

        string baseUrl = Regex.Replace(indexUrl, @"\.html$", "");
        var chapterUrls = Regex.Matches(html, @"href=[""']?([^""'\s>]+)[""']?", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value)
            .Select(h => h.StartsWith("http") ? h : new Uri(new Uri(indexUrl), h).ToString())
            .Where(u => u.StartsWith(baseUrl + "_") && u.EndsWith(".html"))
            .Distinct()
            .OrderBy(u => { var m = Regex.Match(u, @"_(\d+)\.html$"); return m.Success ? int.Parse(m.Groups[1].Value) : 0; })
            .Select((u, i) => new ChapterRef(u, $"Page {i + 1}"))
            .ToList();

        string? cover = null;
        var og = Regex.Match(html, @"<meta[^>]+property=[""']og:image[""'][^>]+content=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        if (!og.Success) og = Regex.Match(html, @"<meta[^>]+content=[""']([^""']+)[""'][^>]+property=[""']og:image[""']", RegexOptions.IgnoreCase);
        if (og.Success) cover = og.Groups[1].Value.Trim();

        return new IndexInfo(title, author, chapterUrls, cover);
    }

    public List<string> ExtractChapterText(string html)
    {
        html = Regex.Replace(html, @"<script[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<style[\s\S]*?</style>",   "", RegexOptions.IgnoreCase);

        // Body text lives in <article class="article-content">; paging + “bookmark 52shuku” promo are inside
        // the same article after <div class="pagination2">. Prev/next *novel* links sit outside </article>.
        string fragment = TryExtractShukuArticleBody(html) ?? html;

        var result = new List<string>();
        foreach (Match m in Regex.Matches(fragment, @"<p(?:\s[^>]*)?>([^<]*(?:<(?!/p>)[^<]*)*)</p>", RegexOptions.IgnoreCase))
        {
            string inner = m.Groups[1].Value;
            inner = Regex.Replace(inner, @"<[^>]+>", "");
            inner = WebUtility.HtmlDecode(inner);
            inner = inner.Replace("\u3000", " ").Trim();
            if (inner.Length == 0 || IsShukuNoiseLine(inner))
                continue;
            if (Regex.IsMatch(inner, @"[\u4e00-\u9fff\u3400-\u4dbf\uf900-\ufaff]"))
                result.Add(inner);
        }

        return result;
    }

    /// <summary>
    /// Returns inner HTML of the reader article, truncated before the chapter pager and site promo block.
    /// </summary>
    private static string? TryExtractShukuArticleBody(string html)
    {
        var article = Regex.Match(html,
            @"<article[^>]*\barticle-content\b[^>]*>([\s\S]*?)</article>",
            RegexOptions.IgnoreCase);
        if (!article.Success)
            return null;

        string inner = article.Groups[1].Value;

        var cutPager = Regex.Match(inner,
            @"([\s\S]*?)<div[^>]+class\s*=\s*[""'][^""']*\bpagination2\b",
            RegexOptions.IgnoreCase);
        if (cutPager.Success)
            return cutPager.Groups[1].Value;

        // Older/alternate templates: promo block after <hr>
        var cutPromo = Regex.Match(inner,
            @"([\s\S]*?)<hr\s*/?>\s*(?:<p[^>]*>)?\s*哦豁",
            RegexOptions.IgnoreCase);
        if (cutPromo.Success)
            return cutPromo.Groups[1].Value;

        return inner;
    }

    private static bool IsShukuNoiseLine(string t)
    {
        if (Regex.IsMatch(t,
                @"(^|\s)(目录|上一页|下一页|尾页)(\s|$)|52书库不错|收藏网址\s*https?://www\.52shuku\.net|传送门：|排行榜单|更多精彩小说推荐|专题推荐|本站内容来源互联网|版权&反馈|关于我们",
                RegexOptions.IgnoreCase))
            return true;

        if (Regex.IsMatch(t,
                @"上一篇：|下一篇：|Table\s+of\s+contents|Previous\s+page|Next\s+page|Last\s+page|bookmark\s+the\s+URL|Portal:|Leaderboard|More\s+wonderful\s+novel|Special\s+Recommendation",
                RegexOptions.IgnoreCase))
            return true;

        if (Regex.IsMatch(t, @"^\s*Top\s*$", RegexOptions.IgnoreCase))
            return true;

        return false;
    }
}
