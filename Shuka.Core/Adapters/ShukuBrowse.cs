using System.Linq;
using System.Text.RegularExpressions;

namespace Shuka.Core.Adapters;

/// <summary>
/// Browse/discover support for 52shuku.net (52书库).
///
/// The site is a curated Chinese BL/danmei/romance novel library.
/// Search is disabled on the site ("search is close").
///
/// Recent (modern danmei):  https://www.52shuku.net/xiandaidushi/
///                          https://www.52shuku.net/xiandaidushi/index_{page}.html  (page 2+)
/// Popular (romance):       https://www.52shuku.net/yanqing/
///                          https://www.52shuku.net/yanqing/index_{page}.html       (page 2+)
///
/// Book URL format: https://www.52shuku.net/{category}/{folder}/bk{id}.html
/// No Cloudflare protection.
/// </summary>
public class ShukuBrowse : IBrowsableAdapter
{
    public string SiteName         => "52shuku.net";
    public string Description      => "Curated BL · danmei & romance";
    public string IconGlyph        => "\uE894"; // language (globe)
    public bool   RequiresCfBypass => false;

    public string GetRecentUrl(int page = 1) =>
        page == 1
            ? "https://www.52shuku.net/xiandaidushi/"
            : $"https://www.52shuku.net/xiandaidushi/index_{page}.html";

    public string GetPopularUrl(int page = 1) =>
        page == 1
            ? "https://www.52shuku.net/yanqing/"
            : $"https://www.52shuku.net/yanqing/index_{page}.html";

    public string GetTop500Url(int page = 1) =>
        page == 1
            ? "https://www.52shuku.net/tuijian/gl_top.html"
            : $"https://www.52shuku.net/tuijian/gl_top_{page}.html";

    public IReadOnlyList<SourceFilter>? Filters => new SourceFilter[]
    {
        new("Modern Danmei",        page => page == 1 ? "https://www.52shuku.net/xiandaidushi/" : $"https://www.52shuku.net/xiandaidushi/index_{page}.html"),
        new("Transmigration Danmei", page => page == 1 ? "https://www.52shuku.net/chuanyue/"     : $"https://www.52shuku.net/chuanyue/index_{page}.html"),
        new("Ancient Danmei",       page => page == 1 ? "https://www.52shuku.net/jiakong/"      : $"https://www.52shuku.net/jiakong/index_{page}.html"),
        new("GL / Baihe",           page => page == 1 ? "https://www.52shuku.net/gl/"           : $"https://www.52shuku.net/gl/index_{page}.html"),
        new("BL / Fanfic",          page => page == 1 ? "https://www.52shuku.net/bl/"           : $"https://www.52shuku.net/bl/index_{page}.html"),
        new("Top 500",              page => page == 1 ? "https://www.52shuku.net/tuijian/gl_top.html" : $"https://www.52shuku.net/tuijian/gl_top_{page}.html"),
    };

    // Search is disabled on the site — fall back to recent listing but include query for local filtering
    public string GetSearchUrl(string query, int page = 1) =>
        GetRecentUrl(page) + "?q=" + Uri.EscapeDataString(query);

    public ListingPage ParseListing(string html, string pageUrl)
    {
        var novels = new List<NovelEntry>();
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Extract query from pageUrl if present, to filter results locally
        string? query = null;
        var queryMatch = Regex.Match(pageUrl, @"[?&]q=([^&]+)");
        if (queryMatch.Success)
            query = Uri.UnescapeDataString(queryMatch.Groups[1].Value);

        // Each novel entry on 52shuku listing pages is structured as:
        //   <article class="excerpt">
        //     <header><h2><a href="https://www.52shuku.net/.../bk....html">Title_Author【status】 </a></h2></header>
        //     <span class="note">　　[GL] 《Title》作者：Author【status】　　简介：　　Synopsis text...</span>
        //     ...
        //   </article>

        var articlePattern = new Regex(
            @"<article[^>]*\bexcerpt\b[^>]*>([\s\S]*?)</article>",
            RegexOptions.IgnoreCase);

        var linkPattern = new Regex(
            @"<a\s[^>]*href=[""'](https?://(?:www\.)?52shuku\.net/[^""']+\.html)[""'][^>]*>\s*([^<]{2,120}?)\s*</a>",
            RegexOptions.IgnoreCase);

        var notePattern = new Regex(
            @"<span[^>]*\bnote\b[^>]*>([\s\S]*?)</span>",
            RegexOptions.IgnoreCase);

        foreach (Match am in articlePattern.Matches(html))
        {
            string articleHtml = am.Groups[1].Value;

            // Find the book link in the h2 heading
            var lm = linkPattern.Match(articleHtml);
            if (!lm.Success) continue;

            string url = lm.Groups[1].Value.Trim();

            // Ignore non-novel aggregation / category / top list links and year recommendations (e.g. 2026年...小说推荐)
            if (url.Contains("/tuijian/") || url.Contains("_top") || url.Contains("/Top/") || url.EndsWith("/shuoming.html") || Regex.IsMatch(url, @"\d{4}年"))
                continue;

            // Heading text: "Title_Author【status】"
            string headingText = System.Net.WebUtility.HtmlDecode(
                Regex.Replace(lm.Groups[2].Value, @"<[^>]+>", "").Trim());

            // Skip year recommendation titles (e.g. "2026年综漫同人小说推荐", "2026年...")
            if (Regex.IsMatch(headingText, @"\d{4}年") || headingText.Contains("小说推荐"))
                continue;

            if (!seen.Add(url)) continue;

            // Strip status suffix like 【完结】【完结+番外】
            string cleanHeading = Regex.Replace(headingText, @"[【\[][^】\]]*[】\]]", "").Trim();
            cleanHeading = cleanHeading.TrimEnd('_').Trim();

            // Split on last underscore to separate title from author
            string title  = cleanHeading;
            string? author = null;
            int lastUnderscore = cleanHeading.LastIndexOf('_');
            if (lastUnderscore > 0)
            {
                title  = cleanHeading[..lastUnderscore].Trim();
                author = cleanHeading[(lastUnderscore + 1)..].Trim();
                if (string.IsNullOrWhiteSpace(author)) author = null;
            }

            if (string.IsNullOrWhiteSpace(title) || title.Length < 2 || Regex.IsMatch(title, @"\d{4}年")) continue;

            // Extract synopsis from <span class="note">
            // The note looks like: "　　[GL] 《Title》作者：Author【status】　　简介：　　actual synopsis..."
            // or:                  "　　[GL] 《Title》作者：Author　　文案：　　blurb..."
            string? desc = null;
            var nm = notePattern.Match(articleHtml);
            if (nm.Success)
            {
                string noteRaw = System.Net.WebUtility.HtmlDecode(
                    Regex.Replace(nm.Groups[1].Value, @"<[^>]+>", " ").Trim());
                noteRaw = noteRaw.Replace("\u3000", " ").Trim();

                int tipsIdx = noteRaw.IndexOf("Tips", StringComparison.OrdinalIgnoreCase);
                if (tipsIdx > 0)
                    noteRaw = noteRaw[..tipsIdx].Trim();

                int tagIdx = noteRaw.IndexOf("所属专题", StringComparison.OrdinalIgnoreCase);
                if (tagIdx > 0)
                    noteRaw = noteRaw[..tagIdx].Trim();

                var synM = Regex.Match(noteRaw, @"(?:简介|文案)[：:]\s*(.+)", RegexOptions.Singleline);
                if (synM.Success)
                    desc = Regex.Replace(synM.Groups[1].Value, @"\s+", " ").Trim();
                else
                    desc = noteRaw.Length > 10 ? noteRaw : null;
            }

            var chMeta = ExtractChapterMeta((desc ?? "") + " " + headingText);
            novels.Add(new NovelEntry(title, author, url, null, desc, null, chMeta.count, chMeta.text));
        }

        // Support for Top 500 page entries: <h3>1、<a href="...">Title_Author【status】</a></h3>
        var h3Pattern = new Regex(
            @"<h3[^>]*>\s*(?:\d+[\u3001\.、])?\s*<a\s[^>]*href=[""'](https?://(?:www\.)?52shuku\.net/[^""']+\.html)[""'][^>]*>\s*([^<]{2,120}?)\s*</a>\s*</h3>",
            RegexOptions.IgnoreCase);

        foreach (Match hm in h3Pattern.Matches(html))
        {
            string url = hm.Groups[1].Value.Trim();
            if (url.Contains("/tuijian/") || url.Contains("_top") || url.Contains("/Top/") || Regex.IsMatch(url, @"\d{4}年")) continue;

            string headingText = System.Net.WebUtility.HtmlDecode(
                Regex.Replace(hm.Groups[2].Value, @"<[^>]+>", "").Trim());

            if (Regex.IsMatch(headingText, @"\d{4}年") || headingText.Contains("小说推荐")) continue;
            if (!seen.Add(url)) continue;

            string cleanHeading = Regex.Replace(headingText, @"[【\[][^】\]]*[】\]]", "").Trim();
            cleanHeading = cleanHeading.TrimEnd('_').Trim();

            string title = cleanHeading;
            string? author = null;
            int lastUnderscore = cleanHeading.LastIndexOf('_');
            if (lastUnderscore > 0)
            {
                title = cleanHeading[..lastUnderscore].Trim();
                author = cleanHeading[(lastUnderscore + 1)..].Trim();
                if (string.IsNullOrWhiteSpace(author)) author = null;
            }

            if (string.IsNullOrWhiteSpace(title) || title.Length < 2 || Regex.IsMatch(title, @"\d{4}年")) continue;

            string? desc = null;
            int blockStart = hm.Index + hm.Length;
            int blockEnd = Math.Min(html.Length, blockStart + 600);
            string block = html.Substring(blockStart, blockEnd - blockStart);
            var descM = Regex.Match(block, @"<p[^>]*>([\s\S]*?)</p>", RegexOptions.IgnoreCase);
            if (descM.Success)
            {
                string noteRaw = System.Net.WebUtility.HtmlDecode(
                    Regex.Replace(descM.Groups[1].Value, @"<[^>]+>", " ").Trim());
                noteRaw = noteRaw.Replace("\u3000", " ").Trim();
                var synM = Regex.Match(noteRaw, @"(?:简介|文案)[：:]\s*　*(.{10,600})", RegexOptions.Singleline);
                desc = synM.Success ? synM.Groups[1].Value.Trim() : (noteRaw.Length > 10 ? noteRaw : null);
            }

            var chMeta = ExtractChapterMeta((desc ?? "") + " " + headingText);
            novels.Add(new NovelEntry(title, author, url, null, desc, null, chMeta.count, chMeta.text));
        }

        // Fallback: scan for any 52shuku book links with adjacent text
        if (novels.Count == 0)
        {
            var linkFallback = new Regex(
                @"href=[""'](https?://(?:www\.)?52shuku\.net/[^""']+/bk[^""']+\.html)[""'][^>]*>\s*([^<]{2,80})\s*</a>",
                RegexOptions.IgnoreCase);
            foreach (Match m in linkFallback.Matches(html))
            {
                string url = m.Groups[1].Value;
                if (url.Contains("/tuijian/") || Regex.IsMatch(url, @"\d{4}年")) continue;
                if (!seen.Add(url)) continue;
                string rawTitle = System.Net.WebUtility.HtmlDecode(m.Groups[2].Value.Trim());
                string title = Regex.Replace(rawTitle, @"[【\[][^】\]]*[】\]]", "").Trim();
                if (title.Length < 2 || Regex.IsMatch(title, @"\d{4}年") || title.Contains("小说推荐")) continue;
                novels.Add(new NovelEntry(title, null, url, null, null, null));
            }
        }

        // Apply local query filtering if present
        if (!string.IsNullOrEmpty(query))
        {
            novels = novels.Where(n =>
                (n.Title != null && n.Title.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                (n.Author != null && n.Author.Contains(query, StringComparison.OrdinalIgnoreCase))
            ).ToList();
        }

        // Pagination: pages use index_{page}.html
        bool hasNext = html.Contains("下一页") || html.Contains("next page", StringComparison.OrdinalIgnoreCase);
        int currentPage = 1;
        var pageM = Regex.Match(pageUrl, @"index_(\d+)\.html$");
        if (pageM.Success) int.TryParse(pageM.Groups[1].Value, out currentPage);

        return new ListingPage(novels, hasNext && novels.Count > 0, currentPage);
    }

    private static (int? count, string? text) ExtractChapterMeta(string window)
    {
        var cn = Regex.Match(window, @"(?:共|总)?\s*([0-9]{1,5})\s*章");
        if (cn.Success && int.TryParse(cn.Groups[1].Value, out int cnCount) && cnCount > 0)
            return (cnCount, $"{cnCount} ch");
        var en = Regex.Match(window, @"\b([0-9]{1,5})\s*chapters?\b", RegexOptions.IgnoreCase);
        if (en.Success && int.TryParse(en.Groups[1].Value, out int enCount) && enCount > 0)
            return (enCount, $"{enCount} ch");
        return (null, null);
    }
}
