using System.Net;
using System.Text.RegularExpressions;

namespace Shuka.Core.Adapters;

// 52shuku.net adapter
public class ShukuAdapter : ISiteAdapter
{
    public string SiteName => "52shuku.net";

    public bool Matches(string url) =>
        url.Contains("52shuku.net", StringComparison.OrdinalIgnoreCase) &&
        !url.Contains("/tuijian/", StringComparison.OrdinalIgnoreCase) &&
        !url.Contains("_top", StringComparison.OrdinalIgnoreCase) &&
        !Regex.IsMatch(url, @"\d{4}年", RegexOptions.IgnoreCase);

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
        if (!am.Success)
            am = Regex.Match(html, @"Author[：:\s]+([^\n<\[【]{2,40})", RegexOptions.IgnoreCase);
        if (am.Success) author = am.Groups[1].Value.Trim();

        // ── Synopsis ──────────────────────────────────────────────────────────
        // 52shuku index pages have the structure:
        //   <article class="article-content">
        //     <p>小说简介：</p>
        //     <p>... 简介：... summary text ...</p>
        //     <p>所属专题：...</p>
        //     <p>Tips：如果觉得52不错...</p>
        //   </article>
        string? synopsis = null;

        var articleM = Regex.Match(html,
            @"<article[^>]*\barticle-content\b[^>]*>([\s\S]*?)</article>",
            RegexOptions.IgnoreCase);
        string scopeHtml = articleM.Success ? articleM.Groups[1].Value : html;

        // Cut at Tips: or 所属专题: or con_pc link or mulu list
        var cutTips = Regex.Match(scopeHtml,
            @"([\s\S]*?)(?:Tips[：:]|所属专题[：:]|<p[^>]*class=[""']con_pc|<ul[^>]*class=[""']list)",
            RegexOptions.IgnoreCase);
        if (cutTips.Success)
            scopeHtml = cutTips.Groups[1].Value;

        string cleanText = System.Net.WebUtility.HtmlDecode(
            Regex.Replace(scopeHtml, @"<[^>]+>", " "));
        cleanText = cleanText.Replace("\u3000", " ").Trim();

        var synMatch = Regex.Match(cleanText,
            @"(?:简介|文案)[：:]\s*(.+)",
            RegexOptions.Singleline);
        if (synMatch.Success)
        {
            synopsis = Regex.Replace(synMatch.Groups[1].Value, @"\s+", " ").Trim();
        }
        else
        {
            var altMatch = Regex.Match(cleanText,
                @"小说简介[：:]\s*(.+)",
                RegexOptions.Singleline);
            if (altMatch.Success)
                synopsis = Regex.Replace(altMatch.Groups[1].Value, @"\s+", " ").Trim();
        }

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
            if (inner.Length == 0)
                continue;

            // Immediately stop processing if reaching pager links (目录, 上一页, 下一页, 尾页) or footer text
            if (IsShukuCutoffLine(inner))
                break;

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

        // Cut at pagination2 div or list
        var cutPager = Regex.Match(inner,
            @"([\s\S]*?)<div[^>]*\bpagination2\b",
            RegexOptions.IgnoreCase);
        if (cutPager.Success)
            inner = cutPager.Groups[1].Value;

        var cutList = Regex.Match(inner,
            @"([\s\S]*?)<ul[^>]*\blist\b",
            RegexOptions.IgnoreCase);
        if (cutList.Success)
            inner = cutList.Groups[1].Value;

        // Cut before navigation block containing 目录 / 上一页 / 下一页 / 尾页
        var cutNav = Regex.Match(inner,
            @"([\s\S]*?)(?:<a[^>]*>[\s\S]*?)?(?:目录|上一页|下一页|尾页)",
            RegexOptions.IgnoreCase);
        if (cutNav.Success)
            inner = cutNav.Groups[1].Value;

        // Cut before promo block after <hr> or footer
        var cutPromo = Regex.Match(inner,
            @"([\s\S]*?)<hr\s*/?>\s*(?:<p[^>]*>)?\s*(?:哦豁|52书库|传送门|排行榜单)",
            RegexOptions.IgnoreCase);
        if (cutPromo.Success)
            inner = cutPromo.Groups[1].Value;

        return inner;
    }

    private static bool IsShukuCutoffLine(string t)
    {
        if (Regex.IsMatch(t,
                @"目录|上一页|下一页|尾页|52书库|收藏网址|传送门|排行榜单|更多精彩小说推荐|专题推荐|本站内容来源互联网|版权&反馈|关于我们|上一篇|下一篇|返回列表|哦豁",
                RegexOptions.IgnoreCase))
            return true;

        if (Regex.IsMatch(t,
                @"Table\s+of\s+contents|Previous\s+page|Next\s+page|Last\s+page|bookmark\s+the\s+URL|Portal|Leaderboard|More\s+wonderful\s+novel|Special\s+Recommendation",
                RegexOptions.IgnoreCase))
            return true;

        if (Regex.IsMatch(t, @"^\s*Top\s*$", RegexOptions.IgnoreCase))
            return true;

        return false;
    }
}
