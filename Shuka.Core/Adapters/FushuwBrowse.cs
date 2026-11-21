using System.Text;
using System.Text.RegularExpressions;

namespace Shuka.Core.Adapters;

/// <summary>
/// Browse/discover support for m.fushuw.org (福书网).
///
/// Recent:  https://m.fushuw.org/news_last/                   (page 1)
///          https://m.fushuw.org/news_last/index_{page}.html  (page 2+)
/// Popular: https://m.fushuw.org/bzremenmonth/                (monthly ranking)
/// Search:  https://m.fushuw.org/e/search/indexsearch.php     (ECMS POST, GBK-encoded)
///
/// Book URLs: https://m.fushuw.org/{category}/{numericId}.html
/// No Cloudflare protection.
/// </summary>
public class FushuwBrowse : IBrowsableAdapter
{
    public string SiteName => "fushuw.org";
    public string Description => "Chinese novels";
    public string IconGlyph => "\uE894"; // language (globe)
    public bool RequiresCfBypass => false;
    public string HomeUrl => "https://m.fushuw.org/";

    public string GetRecentUrl(int page = 1) =>
        page == 1
            ? "https://m.fushuw.org/news_last/"
            : $"https://m.fushuw.org/news_last/index_{page}.html";

    public string GetPopularUrl(int page = 1) =>
        page == 1
            ? "https://m.fushuw.org/bzremenmonth/"
            : $"https://m.fushuw.org/bzremenmonth/index_{page}.html";

    public string GetSearchUrl(string query, int page = 1) =>
        "https://m.fushuw.org/e/search/indexsearch.php";

    public (string postBody, string charset)? GetSearchPostBody(string query, int page = 1)
    {
        // ECMS search requires a GBK-encoded POST body — same pattern as dmxs.org
        var gbk = Encoding.GetEncoding("gbk");
        var bytes = gbk.GetBytes(query);
        // Percent-encode each byte that is non-ASCII; leave ASCII chars as-is
        var encodedQuery = string.Concat(bytes.Select(b =>
            b < 128 ? ((char)b).ToString() : $"%{b:X2}"));
        string body = $"keyboard={encodedQuery}&show=title&classid=0";
        return (body, "gb2312");
    }

    public IReadOnlyList<SourceFilter>? Filters => _filters;
    private static readonly SourceFilter[] _filters =
    [        
        new SourceFilter("Monthly", page => page == 1
            ? "https://m.fushuw.org/bzremenmonth/"
            : $"https://m.fushuw.org/bzremenmonth/index_{page}.html"),
        new SourceFilter("Weekly", page => page == 1
            ? "https://m.fushuw.org/bzremenweek/"
            : $"https://m.fushuw.org/bzremenweek/index_{page}.html"),
            new SourceFilter("GL", page => page == 1
            ? "https://m.fushuw.org/GLbaihe/"
            : $"https://m.fushuw.org/GLbaihe/index_{page}.html"),
        new SourceFilter("BL", page => page == 1
            ? "https://m.fushuw.org/BLtongren/"
            : $"https://m.fushuw.org/BLtongren/index_{page}.html"),
    ];

    public ListingPage ParseListing(string html, string pageUrl)
    {
        var novels = new List<NovelEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // fushuw book URLs: /{category}/{numericId}.html
        // e.g. /chuanyuechongsheng/41341.html  /GLbaihe/1234.html
        // Chapter URLs contain an underscore before the page number and must be excluded:
        // e.g. /chuanyuechongsheng/41341_2.html  → NOT a book index

        var blockPattern = new Regex(
            @"<a\s+href=[""']((?:https?://m\.fushuw\.org)?/[a-zA-Z0-9]+/(\d+)\.html)[""'][^>]*>([\s\S]*?)</a>",
            RegexOptions.IgnoreCase);

        foreach (Match m in blockPattern.Matches(html))
        {
            string href   = m.Groups[1].Value;
            string bookId = m.Groups[2].Value;
            string inner  = m.Groups[3].Value;

            if (string.IsNullOrEmpty(bookId)) continue;
            // Skip pagination links (e.g. /GLbaihe/index_2.html)
            if (href.Contains("index_")) continue;

            string url = href.StartsWith("http")
                ? href
                : "https://m.fushuw.org" + href;

            if (!seen.Add(bookId)) continue;

            // Title: strip HTML tags from link inner text
            string title = Regex.Replace(inner, @"<[^>]+>", "").Trim();
            title = System.Net.WebUtility.HtmlDecode(title);

            // The recent page format may embed author inline: "Title 作者：AuthorName"
            string? author = null;
            var authInTitle = Regex.Match(title, @"\s+作者[：:]\s*([^\s\[【]+)");
            if (authInTitle.Success)
            {
                author = authInTitle.Groups[1].Value.Trim();
                title  = title[..authInTitle.Index].Trim();
            }

            // Strip trailing date/size noise like "[05-08]"
            title = Regex.Replace(title, @"\s*[\[\(（【]\d{2,4}[-/]\d{2}[-/]?\d{0,2}[\]\)）】]?\s*$", "").Trim();

            if (title.Length < 2) continue;

            // Cover: look for d/file/p/ image inside the same <a> block
            string? cover = null;
            var imgM = Regex.Match(inner,
                @"src=[""'](https?://[^""']*fushuw\.org/d/file/[^""']+\.(?:jpg|jpeg|png|webp))[""']",
                RegexOptions.IgnoreCase);
            if (!imgM.Success)
                imgM = Regex.Match(inner,
                    @"data-original=[""'](https?://[^""']*fushuw\.org/d/file/[^""']+\.(?:jpg|jpeg|png|webp))[""']",
                    RegexOptions.IgnoreCase);
            if (imgM.Success)
            {
                cover = imgM.Groups[1].Value.Trim();
                // Upgrade http → https
                if (cover.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                    cover = "https://" + cover[7..];
            }

            // Look for author in nearby text (~400 chars around the link)
            int winStart = Math.Max(0, m.Index - 400);
            int winLen   = Math.Min(html.Length - winStart, 900);
            string window = html.Substring(winStart, winLen);

            if (string.IsNullOrWhiteSpace(author))
            {
                var authNearby = Regex.Match(window, @"作者[：:]\s*([^\s<,，\n]{1,30})", RegexOptions.IgnoreCase);
                if (authNearby.Success) author = authNearby.Groups[1].Value.Trim();
            }

            var chapterMeta = ExtractChapterMeta(window);

            novels.Add(new NovelEntry(title, author, url, cover, null, null, chapterMeta.count, chapterMeta.text));
        }

        // Fallback: scan for any /{category}/{id}.html links with adjacent text
        if (novels.Count == 0)
        {
            var linkPattern = new Regex(
                @"href=[""'](?:https?://m\.fushuw\.org)?(/[a-zA-Z0-9]+/(\d+)\.html)[""'][^>]*>\s*([^<]{2,80})\s*</a>",
                RegexOptions.IgnoreCase);
            foreach (Match m in linkPattern.Matches(html))
            {
                string bookId = m.Groups[2].Value;
                if (!seen.Add(bookId)) continue;
                string title = System.Net.WebUtility.HtmlDecode(m.Groups[3].Value.Trim());
                if (title.Length < 2) continue;
                string url = "https://m.fushuw.org" + m.Groups[1].Value;
                novels.Add(new NovelEntry(title, null, url, null, null, null));
            }
        }

        // Pagination
        bool hasNext = html.Contains("下一页") ||
                       Regex.IsMatch(html, @"index_\d+\.html", RegexOptions.IgnoreCase);
        int currentPage = 1;
        var pageM = Regex.Match(pageUrl, @"index_(\d+)\.html$");
        if (pageM.Success) int.TryParse(pageM.Groups[1].Value, out currentPage);

        return new ListingPage(novels, hasNext && novels.Count > 0, currentPage);
    }

    private static (int? count, string? text) ExtractChapterMeta(string sample)
    {
        if (string.IsNullOrWhiteSpace(sample))
            return (null, null);

        var cn = Regex.Match(sample, @"(?:共|总)?\s*([0-9]{1,5})\s*章");
        if (cn.Success && int.TryParse(cn.Groups[1].Value, out int cnCount))
            return (cnCount, $"{cnCount} chapters");

        return (null, null);
    }
}
