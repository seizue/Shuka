using System.Text;
using System.Text.RegularExpressions;

namespace Shuka.Core.Adapters;

/// <summary>
/// Browse/discover support for shubaow.net (书宝网).
///
/// Browse listings use tables:
///   Rankings:   /top/{type}/    and /top/{type}/{page}.html
///   Categories: /list/{id}.html and /list/{id}/{page}.html
/// Search:      /modules/article/search.php via GBK-encoded POST/GET
/// </summary>
public class ShubaowBrowse : IBrowsableAdapter
{
    public string SiteName => "shubaow.net";
    public string Description => "Romance, BL, GL & general web novels";
    public string IconGlyph => "\uE894"; // globe icon
    public bool RequiresCfBypass => false;

    public string HomeUrl => "https://www.shubaow.net/top/monthvisit/";

    public string GetRecentUrl(int page = 1) =>
        page == 1
            ? "https://www.shubaow.net/top/postdate/"
            : $"https://www.shubaow.net/top/postdate/{page}.html";

    public string GetPopularUrl(int page = 1) =>
        page == 1
            ? "https://www.shubaow.net/top/monthvisit/"
            : $"https://www.shubaow.net/top/monthvisit/{page}.html";

    public string GetSearchUrl(string query, int page = 1) =>
        $"https://www.shubaow.net/modules/article/search.php?searchkey={UrlEncodeGbk(query)}&page={page}";

    public (string postBody, string charset)? GetSearchPostBody(string query, int page = 1)
    {
        string encoded = UrlEncodeGbk(query);
        return ($"searchkey={encoded}", "gbk");
    }

    public IReadOnlyList<SourceFilter>? Filters => new SourceFilter[]
    {
        new("Monthly Views", page => page == 1 ? "https://www.shubaow.net/top/monthvisit/" : $"https://www.shubaow.net/top/monthvisit/{page}.html"),
        new("Monthly Recs", page => page == 1 ? "https://www.shubaow.net/top/monthvote/" : $"https://www.shubaow.net/top/monthvote/{page}.html"),
        new("Weekly Views", page => page == 1 ? "https://www.shubaow.net/top/weekvisit/" : $"https://www.shubaow.net/top/weekvisit/{page}.html"),
        new("Weekly Recs", page => page == 1 ? "https://www.shubaow.net/top/weekvote/" : $"https://www.shubaow.net/top/weekvote/{page}.html"),
        new("All-Time Views", page => page == 1 ? "https://www.shubaow.net/top/allvisit/" : $"https://www.shubaow.net/top/allvisit/{page}.html"),
        new("All-Time Recs", page => page == 1 ? "https://www.shubaow.net/top/allvote/" : $"https://www.shubaow.net/top/allvote/{page}.html"),
        new("Top Favorites", page => page == 1 ? "https://www.shubaow.net/top/goodnum/" : $"https://www.shubaow.net/top/goodnum/{page}.html"),
        new("Site Recs", page => page == 1 ? "https://www.shubaow.net/top/toptime/" : $"https://www.shubaow.net/top/toptime/{page}.html"),
        new("Word Count", page => page == 1 ? "https://www.shubaow.net/top/size/" : $"https://www.shubaow.net/top/size/{page}.html"),
        new("Recent Updates", page => page == 1 ? "https://www.shubaow.net/top/lastupdate/" : $"https://www.shubaow.net/top/lastupdate/{page}.html"),
        new("New Releases", page => page == 1 ? "https://www.shubaow.net/top/postdate/" : $"https://www.shubaow.net/top/postdate/{page}.html"),
        new("Romance", page => page == 1 ? "https://www.shubaow.net/list/1.html" : $"https://www.shubaow.net/list/1/{page}.html"),
        new("BL / Danmei", page => page == 1 ? "https://www.shubaow.net/list/2.html" : $"https://www.shubaow.net/list/2/{page}.html"),
        new("GL / Baihe", page => page == 1 ? "https://www.shubaow.net/list/3.html" : $"https://www.shubaow.net/list/3/{page}.html"),
        new("Others", page => page == 1 ? "https://www.shubaow.net/list/4.html" : $"https://www.shubaow.net/list/4/{page}.html"),
    };

    public ListingPage ParseListing(string html, string pageUrl)
    {
        var novels = new List<NovelEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pattern 1: Table row listings (ranking, category lists, search results)
        var rowPattern = new Regex(
            @"<tr[^>]*>([\s\S]*?)</tr>",
            RegexOptions.IgnoreCase);

        foreach (Match row in rowPattern.Matches(html))
        {
            string content = row.Groups[1].Value;

            var linkM = Regex.Match(content,
                @"href=[""'](?:https?://(?:www\.)?shubaow\.net)?/book/(\d+)\.html[""'][^>]*>\s*([^<]{2,120})\s*</a>",
                RegexOptions.IgnoreCase);
            if (!linkM.Success) continue;

            string bookId = linkM.Groups[1].Value;
            if (!seen.Add(bookId)) continue;

            string title = System.Net.WebUtility.HtmlDecode(linkM.Groups[2].Value.Trim());
            if (string.IsNullOrWhiteSpace(title) || title.Length < 2) continue;

            string url = $"https://www.shubaow.net/book/{bookId}.html";

            // Author
            string? author = null;
            var authM = Regex.Match(content, @"<td[^>]*class=[""'][^""']*text-muted[^""']*[""'][^>]*>\s*([^<]{1,40})\s*</td>", RegexOptions.IgnoreCase);
            if (!authM.Success)
                authM = Regex.Match(content, @"作者[：:]\s*([^\s<,\n]{1,30})");
            if (authM.Success) author = authM.Groups[1].Value.Trim();

            // Latest chapter
            string? latestCh = null;
            var chM = Regex.Match(content, @"href=[""'][^""']*/book/\d+/\d+\.html[""'][^>]*>\s*([^<]+)\s*</a>", RegexOptions.IgnoreCase);
            if (chM.Success) latestCh = System.Net.WebUtility.HtmlDecode(chM.Groups[1].Value.Trim());

            novels.Add(new NovelEntry(title, author, url, null, null, null, null, latestCh));
        }

        // Fallback: Direct book link scan
        if (novels.Count == 0)
        {
            var linkFallback = new Regex(
                @"href=[""'](?:https?://(?:www\.)?shubaow\.net)?/book/(\d+)\.html[""'][^>]*>\s*([^<]{2,120})\s*</a>",
                RegexOptions.IgnoreCase);
            foreach (Match m in linkFallback.Matches(html))
            {
                string bookId = m.Groups[1].Value;
                if (!seen.Add(bookId)) continue;

                string title = System.Net.WebUtility.HtmlDecode(m.Groups[2].Value.Trim());
                if (title.Length < 2) continue;

                novels.Add(new NovelEntry(title, null, $"https://www.shubaow.net/book/{bookId}.html", null, null, null));
            }
        }

        // Determine current page and hasNext
        int currentPage = 1;
        var pageM = Regex.Match(pageUrl, @"/(\d+)\.html$");
        if (pageM.Success) int.TryParse(pageM.Groups[1].Value, out currentPage);

        bool hasNext = html.Contains("下一页") || html.Contains("next", StringComparison.OrdinalIgnoreCase) ||
                       Regex.IsMatch(html, @"href=[""'][^""']*/" + (currentPage + 1) + @"\.html[""']", RegexOptions.IgnoreCase);

        return new ListingPage(novels, hasNext && novels.Count > 0, currentPage);
    }

    private static string UrlEncodeGbk(string text)
    {
        byte[] bytes = Encoding.GetEncoding("gbk").GetBytes(text);
        return string.Concat(bytes.Select(b => $"%{b:X2}"));
    }
}
