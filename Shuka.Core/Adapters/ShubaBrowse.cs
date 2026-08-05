using System.Text.RegularExpressions;

namespace Shuka.Core.Adapters;

/// <summary>
/// Browse/discover support for 69shuba.com (69书吧).
///
/// Recent:  https://www.69shuba.com/last.html          (no pagination — single page)
/// Popular: https://www.69shuba.com/novels/monthvisit_0_0_{page}.htm
/// Search:  https://www.69shuba.com/search.htm?searchkey={query}&amp;page={page}
///
/// Book URLs use the .htm extension: /book/{id}.htm
/// No Cloudflare — site is accessible without bypass.
/// </summary>
public class ShubaBrowse : IBrowsableAdapter
{
    public string SiteName         => "69shuba.com";
    public string Description      => "Chinese novels · fantasy & urban · CF";
    public string IconGlyph        => "\uE894"; // language (globe)
    public bool   RequiresCfBypass => true;

    public string GetRecentUrl(int page = 1)  =>
        // /last.html is a single-page recent list; ignore page param beyond 1
        page == 1 ? "https://www.69shuba.com/last.html"
                  : $"https://www.69shuba.com/novels/newhot_0_0_{page}.htm";

    public string GetPopularUrl(int page = 1) =>
        $"https://www.69shuba.com/novels/monthvisit_0_0_{page}.htm";

    public string GetSearchUrl(string query, int page = 1) =>
        $"https://www.69shuba.com/search.htm?searchkey={Uri.EscapeDataString(query)}&page={page}";

    public ListingPage ParseListing(string html, string pageUrl)
    {
        var novels = new List<NovelEntry>();
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Book links: /book/{id}.htm  (the site changed from /book/{id}/ to /book/{id}.htm)
        // The popular/ranking pages wrap each entry in an <li> with rank number + title + author
        // The recent page (/last.html) has simple <a href="/book/{id}.htm">Title</a> links

        // Pattern 1: structured ranking blocks — <li> containing /book/{id}.htm
        var blockPattern = new Regex(
            @"<li[^>]*>([\s\S]*?)</li>",
            RegexOptions.IgnoreCase);

        foreach (Match block in blockPattern.Matches(html))
        {
            string content = block.Groups[1].Value;

            var urlM = Regex.Match(content,
                @"href=[""'](?:https?://(?:www\.)?69shuba\.com)?/book/(\d+)\.htm[""']",
                RegexOptions.IgnoreCase);
            if (!urlM.Success) continue;

            string bookId = urlM.Groups[1].Value;
            if (!seen.Add(bookId)) continue;

            string url = $"https://www.69shuba.com/book/{bookId}.htm";

            // Title: prefer <h3> or <h4>, fall back to link text
            string title = "";
            var titleM = Regex.Match(content,
                @"<h[34][^>]*>([^<]+)</h[34]>",
                RegexOptions.IgnoreCase);
            if (titleM.Success) title = System.Net.WebUtility.HtmlDecode(titleM.Groups[1].Value.Trim());
            if (string.IsNullOrWhiteSpace(title))
            {
                var aM = Regex.Match(content,
                    @"href=[""'][^""']*/book/" + Regex.Escape(bookId) + @"\.htm[""'][^>]*>\s*([^<]{2,80})\s*</a>",
                    RegexOptions.IgnoreCase);
                if (aM.Success) title = System.Net.WebUtility.HtmlDecode(aM.Groups[1].Value.Trim());
            }
            if (string.IsNullOrWhiteSpace(title)) continue;

            // Cover image
            string? cover = null;
            var imgM = Regex.Match(content,
                @"<img[^>]+src=[""'](https?://[^""']+)[""']",
                RegexOptions.IgnoreCase);
            if (imgM.Success) cover = imgM.Groups[1].Value;
            if (cover == null && int.TryParse(bookId, out int bid))
                cover = $"https://cdn.cdnshu.com/files/article/image/{bid / 1000}/{bookId}/{bookId}s.jpg";

            // Author
            string? author = null;
            var authM = Regex.Match(content, @"作者[：:]\s*([^\s<,，]{1,30})");
            if (!authM.Success)
                authM = Regex.Match(content, @"<p[^>]*>\s*([^\s<]{2,20})\s*(?:连载|全本)");
            if (authM.Success) author = authM.Groups[1].Value.Trim();

            // Description
            string? desc = null;
            var descM = Regex.Match(content,
                @"<p[^>]*class=""[^""]*(?:desc|intro|summary)[^""]*""[^>]*>([^<]{10,})</p>",
                RegexOptions.IgnoreCase);
            if (descM.Success) desc = System.Net.WebUtility.HtmlDecode(descM.Groups[1].Value.Trim());

            var chMeta = ExtractChapterMeta(content);
            novels.Add(new NovelEntry(title, author, url, cover, desc, null, chMeta.count, chMeta.text));
        }

        // Fallback: direct /book/{id}.htm link scan (works for /last.html and search results)
        if (novels.Count == 0)
        {
            var linkPattern = new Regex(
                @"href=[""'](?:https?://(?:www\.)?69shuba\.com)?/book/(\d+)\.htm[""'][^>]*>\s*([^<]{2,80})\s*</a>",
                RegexOptions.IgnoreCase);
            foreach (Match m in linkPattern.Matches(html))
            {
                string bookId = m.Groups[1].Value;
                if (!seen.Add(bookId)) continue;
                string title = System.Net.WebUtility.HtmlDecode(m.Groups[2].Value.Trim());
                if (title.Length < 2) continue;
                string? cover = int.TryParse(bookId, out int bid)
                    ? $"https://cdn.cdnshu.com/files/article/image/{bid / 1000}/{bookId}/{bookId}s.jpg"
                    : null;
                novels.Add(new NovelEntry(title, null,
                    $"https://www.69shuba.com/book/{bookId}.htm", cover, null, null));
            }
        }

        // Pagination: ranking pages use /novels/monthvisit_0_0_{page}.htm pattern
        bool hasNext = html.Contains("下一页") ||
                       Regex.IsMatch(html, @"monthvisit_0_0_\d+\.htm", RegexOptions.IgnoreCase);
        int currentPage = 1;
        var pageM = Regex.Match(pageUrl, @"_(\d+)\.htm$");
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
