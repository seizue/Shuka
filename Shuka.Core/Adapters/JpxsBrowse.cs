using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Shuka.Core.Adapters;

/// <summary>
/// Browse/discover support for jpxs123.com (精品小說網).
///
/// Home:    https://jpxs123.com/book/
/// Recent:  https://jpxs123.com/book/               (page 1)
///          https://jpxs123.com/book/index_{page}.html (page 2+)
/// Search:  ECMS POST to https://jpxs123.com/e/search/indexsearch.php
///          (GBK-encoded body, same as dmxs.org)
///
/// Book URLs: https://jpxs123.com/{category}/{id}.html
/// No Cloudflare, GBK encoding (auto-detected by HttpFetcher).
/// </summary>
public class JpxsBrowse : IBrowsableAdapter
{
    public string SiteName => "jpxs123.com";
    public string Description => "Chinese novels";
    public string IconGlyph => "\uE894"; // language (globe)
    public bool RequiresCfBypass => false;

    public string HomeUrl => "https://jpxs123.com/book/";

    public string GetRecentUrl(int page = 1) =>
        page == 1
            ? "https://jpxs123.com/book/"
            : $"https://jpxs123.com/book/index_{page}.html";

    public string GetPopularUrl(int page = 1) =>
        // /book/ is the site's main (most recently updated) listing
        GetRecentUrl(page);

    public IReadOnlyList<SourceFilter>? Filters => _filters;
    private static readonly SourceFilter[] _filters =
    [
        new SourceFilter("Urban",      page => CategoryUrl("dsxs", page)),
        new SourceFilter("Xuanhuan",   page => CategoryUrl("xh", page)),
        new SourceFilter("Historical", page => CategoryUrl("lishi", page)),
        new SourceFilter("Isekai",     page => CategoryUrl("cyjk", page)),
        new SourceFilter("Horror",     page => CategoryUrl("guihua", page)),
        new SourceFilter("Fanfic",     page => CategoryUrl("tongren", page)),
    ];

    private static string CategoryUrl(string category, int page) =>
        page == 1
            ? $"https://jpxs123.com/{category}/"
            : $"https://jpxs123.com/{category}/index_{page}.html";

    public string GetSearchUrl(string query, int page = 1) =>
        "https://jpxs123.com/e/search/indexsearch.php";

    public (string postBody, string charset)? GetSearchPostBody(string query, int page = 1)
    {
        // jpxs123.com uses ECMS which requires a GBK-encoded POST to its search endpoint.
        var gbk = Encoding.GetEncoding("gbk");
        var bytes = gbk.GetBytes(query);
        var encodedQuery = string.Concat(bytes.Select(b =>
            b < 128 ? ((char)b).ToString() : $"%{b:X2}"));
        string body = $"keyboard={encodedQuery}&show=title&classid=0";
        return (body, "gb2312");
    }

    public ListingPage ParseListing(string html, string pageUrl)
    {
        var novels = new List<NovelEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Card structure:
        // <div class="bk"><a href="/{cat}/{id}.html">
        //   <div class="pic"><img src="/d/file/p/...jpg"></div>
        //   <div class="infos"><h3>Title</h3>
        //     <div class="booknews">作者:Author <label class="size">14.47 MB</label> <label class="date">2026-11-22</label></div>
        //     <p>Synopsis...</p></div>
        // </a></div>
        // Anchor-level capture avoids nested-div truncation issues.
        var anchorPattern = new Regex(
            @"<a\s+href=[""']((?:https?://(?:www\.)?jpxs123\.com)?/([a-zA-Z0-9]+)/(\d+)\.html)[""'][^>]*>([\s\S]*?)</a>",
            RegexOptions.IgnoreCase);

        foreach (Match m in anchorPattern.Matches(html))
        {
            string href = m.Groups[1].Value;
            string bookId = m.Groups[3].Value;
            string inner = m.Groups[4].Value;

            if (href.Contains("index_")) continue;

            if (!seen.Add(bookId)) continue;

            string url = href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? href
                : "https://jpxs123.com" + href;

            // Title: <h3> — strip the (1-N) chapter-count suffix
            string title = "";
            var titleM = Regex.Match(inner, @"<h3[^>]*>([^<]+)</h3>", RegexOptions.IgnoreCase);
            if (titleM.Success) title = System.Net.WebUtility.HtmlDecode(titleM.Groups[1].Value.Trim());
            if (string.IsNullOrWhiteSpace(title))
                title = Regex.Replace(inner, @"<[^>]+>", "").Trim();
            title = Regex.Replace(title, @"\s*[\(（]\s*\d+\s*[-–]\s*\d+\s*[\)）]\s*$", "").Trim();
            if (title.Length < 2) continue;

            // Author: from <div class="booknews">作者:AuthorName
            string? author = null;
            var authM = Regex.Match(inner, @"作者[：:]\s*([^\s<]{1,30})", RegexOptions.IgnoreCase);
            if (authM.Success) author = authM.Groups[1].Value.Trim();

            // Cover: /d/file/p/... (make absolute)
            string? cover = null;
            var imgM = Regex.Match(inner,
                @"<img[^>]+src=[""'](/d/file/p/[^""']+\.(?:jpg|jpeg|png|webp|gif))[""']",
                RegexOptions.IgnoreCase);
            if (imgM.Success)
                cover = "https://jpxs123.com" + imgM.Groups[1].Value;

            // Description: first <p> inside .infos
            string? desc = null;
            var descM = Regex.Match(inner, @"<p[^>]*>([^<]{10,})</p>", RegexOptions.IgnoreCase);
            if (descM.Success) desc = System.Net.WebUtility.HtmlDecode(descM.Groups[1].Value.Trim());

            novels.Add(new NovelEntry(title, author, url, cover, desc, null));
        }

        // Fallback: bare link scan
        if (novels.Count == 0)
        {
            var linkPattern = new Regex(
                @"href=[""'](?:https?://(?:www\.)?jpxs123\.com)?/([a-zA-Z0-9]+)/(\d+)\.html[""'][^>]*>\s*([^<]{2,80})\s*</a>",
                RegexOptions.IgnoreCase);
            foreach (Match m in linkPattern.Matches(html))
            {
                string bookId = m.Groups[2].Value;
                if (!seen.Add(bookId)) continue;
                string title = System.Net.WebUtility.HtmlDecode(m.Groups[3].Value.Trim());
                if (title.Length < 2) continue;
                novels.Add(new NovelEntry(title, null,
                    $"https://jpxs123.com/{m.Groups[1].Value}/{bookId}.html", null, null, null));
            }
        }

        // Pagination: <div class="page"><b>1</b> <a href="/cyjk/index_2.html">2</a>… 下一页
        bool hasNext = html.Contains("下一页") || html.Contains("下一頁") ||
                       Regex.IsMatch(html, @"index_\d+\.html", RegexOptions.IgnoreCase);
        int currentPage = 1;
        var pageM = Regex.Match(pageUrl, @"index_(\d+)\.html$");
        if (pageM.Success) int.TryParse(pageM.Groups[1].Value, out currentPage);

        return new ListingPage(novels, hasNext && novels.Count > 0, currentPage);
    }
}
