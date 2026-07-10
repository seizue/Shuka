using System.Text;
using System.Text.RegularExpressions;

namespace Shuka.Core.Adapters;

/// <summary>
/// Browse/discover support for zhenhunxiaoshuo.com (镇魂小说网).
///
/// Recent:  https://www.zhenhunxiaoshuo.com/newbook/           (page 1)
///          https://www.zhenhunxiaoshuo.com/newbook/{page}.html (page 2+)
/// Popular: https://www.zhenhunxiaoshuo.com/paihangbang/        (page 1)
///          https://www.zhenhunxiaoshuo.com/paihangbang/{page}.html (page 2+)
/// Search:  https://www.zhenhunxiaoshuo.com/search/             (POST: keyboard={query})
///
/// Book URLs: https://www.zhenhunxiaoshuo.com/{slug}/
/// No Cloudflare protection.
/// </summary>
public class ZhenhunBrowse : IBrowsableAdapter
{
    public string SiteName        => "zhenhunxiaoshuo.com";
    public string Description     => "Chinese web novels · 镇魂小说网";
    public string IconGlyph       => "\uE894"; // language (globe)
    public bool   RequiresCfBypass => false;

    public string GetRecentUrl(int page = 1) =>
        page == 1
            ? "https://www.zhenhunxiaoshuo.com/newbook/"
            : $"https://www.zhenhunxiaoshuo.com/newbook/{page}.html";

    public string GetPopularUrl(int page = 1) =>
        page == 1
            ? "https://www.zhenhunxiaoshuo.com/paihangbang/"
            : $"https://www.zhenhunxiaoshuo.com/paihangbang/{page}.html";

    public string GetSearchUrl(string query, int page = 1) =>
        "https://www.zhenhunxiaoshuo.com/search/";

    public (string postBody, string charset)? GetSearchPostBody(string query, int page = 1)
    {
        // Try UTF-8 form POST first; site may also accept GBK — use UTF-8 percent-encoding
        string encoded = Uri.EscapeDataString(query);
        return ($"keyboard={encoded}", "utf-8");
    }

    public ListingPage ParseListing(string html, string pageUrl)
    {
        var novels = new List<NovelEntry>();
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Book URLs are slug-only paths: href="/slug/" or full https://...
        // Pattern grabs the slug, rejects purely numeric slugs (chapter URLs) and
        // known non-book paths.
        var linkRe = new Regex(
            @"href=[""'](?:https?://(?:www\.)?zhenhunxiaoshuo\.com)?/([a-z0-9][a-z0-9\-]*[a-z0-9])/[""']",
            RegexOptions.IgnoreCase);

        // Blacklist path segments that are site navigation, not books
        var skipSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "newbook", "paihangbang", "search", "tag", "author",
            "category", "list", "complete", "update", "img", "css", "js"
        };

        foreach (Match m in linkRe.Matches(html))
        {
            string slug = m.Groups[1].Value;
            if (skipSlugs.Contains(slug)) continue;
            if (!seen.Add(slug)) continue;

            string url = $"https://www.zhenhunxiaoshuo.com/{slug}/";

            // Grab a context window around the link for metadata extraction
            int winStart = Math.Max(0, m.Index - 600);
            int winLen   = Math.Min(html.Length - winStart, 1200);
            string window = html.Substring(winStart, winLen);

            // Title: look for title= attribute on the <a>, or adjacent <h2>/<h3>
            string title = "";
            var titleAttr = Regex.Match(m.Value, @"title=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            if (titleAttr.Success)
                title = System.Net.WebUtility.HtmlDecode(titleAttr.Groups[1].Value.Trim());

            if (string.IsNullOrWhiteSpace(title))
            {
                // inner text of the <a> element itself
                var aFull = Regex.Match(
                    html.Substring(m.Index, Math.Min(300, html.Length - m.Index)),
                    @"<a[^>]*>([^<]{2,80})</a>", RegexOptions.IgnoreCase);
                if (aFull.Success)
                    title = System.Net.WebUtility.HtmlDecode(aFull.Groups[1].Value.Trim());
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                var hTag = Regex.Match(window, @"<h[23][^>]*>([^<]{2,80})</h[23]>", RegexOptions.IgnoreCase);
                if (hTag.Success)
                    title = System.Net.WebUtility.HtmlDecode(hTag.Groups[1].Value.Trim());
            }

            // If we still have no title, fall back to slug as display name
            if (string.IsNullOrWhiteSpace(title))
                title = slug;

            title = Regex.Replace(title, @"\s*(最新章节|全文阅读|免费阅读|在线阅读).*$", "").Trim();
            if (title.Length < 2) continue;

            // Author
            string? author = null;
            var authM = Regex.Match(window,
                @"作者[：:]\s*<a[^>]*>([^<]+)</a>|作者[：:]\s*([^\s<\n,，【】]{1,30})",
                RegexOptions.IgnoreCase);
            if (authM.Success)
                author = (authM.Groups[1].Success ? authM.Groups[1] : authM.Groups[2]).Value.Trim();

            // Cover
            string? cover = null;
            var imgM = Regex.Match(window, @"<img[^>]+src=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            if (imgM.Success)
            {
                cover = imgM.Groups[1].Value.Trim();
                if (!cover.StartsWith("http"))
                    cover = new Uri(new Uri("https://www.zhenhunxiaoshuo.com/"), cover).ToString();
            }

            // Description
            string? desc = null;
            var descM = Regex.Match(window,
                @"<(?:p|div)[^>]*class=[""'][^""']*(?:desc|intro|summary|introduce)[^""']*[""'][^>]*>([^<]{15,})</(?:p|div)>",
                RegexOptions.IgnoreCase);
            if (descM.Success)
                desc = System.Net.WebUtility.HtmlDecode(descM.Groups[1].Value.Trim());

            novels.Add(new NovelEntry(title, author, url, cover, desc, null));
        }

        // Pagination: look for 下一页 or next-page link
        bool hasNext = html.Contains("下一页") ||
                       Regex.IsMatch(html, @"href=[""'][^""']*(?:newbook|paihangbang)/\d+\.html[""']",
                           RegexOptions.IgnoreCase);

        int currentPage = 1;
        var pageM = Regex.Match(pageUrl, @"/(\d+)\.html$");
        if (pageM.Success) int.TryParse(pageM.Groups[1].Value, out currentPage);

        return new ListingPage(novels, hasNext && novels.Count > 0, currentPage);
    }
}
