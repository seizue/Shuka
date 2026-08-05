using System.Text;
using System.Text.RegularExpressions;

namespace Shuka.Core.Adapters;

/// <summary>
/// Browse/discover support for situu.cc (思兔阅读).
///
/// Recent (danmei):   https://www.situu.cc/danmei/
///                    https://www.situu.cc/danmei/index_{page}.html  (page 2+)
/// Popular (yanqing):  https://www.situu.cc/yanqing/
///                    https://www.situu.cc/yanqing/index_{page}.html  (page 2+)
/// Search:            https://www.situu.cc/modules/article/search.php  (POST searchkey={query})
/// </summary>
public class SituuBrowse : IBrowsableAdapter
{
    public string SiteName         => "situu.cc";
    public string Description      => "BL · danmei & romance novels";
    public string IconGlyph        => "\uE894"; // language (globe)
    public bool   RequiresCfBypass => false;

    public string GetRecentUrl(int page = 1) =>
        page == 1
            ? "https://www.situu.cc/danmei/"
            : $"https://www.situu.cc/danmei/index_{page}.html";

    public string GetPopularUrl(int page = 1) =>
        page == 1
            ? "https://www.situu.cc/yanqing/"
            : $"https://www.situu.cc/yanqing/index_{page}.html";

    public string GetSearchUrl(string query, int page = 1) =>
        "https://www.situu.cc/modules/article/search.php";

    public (string postBody, string charset)? GetSearchPostBody(string query, int page = 1)
    {
        // situu.cc uses a GBK-encoded POST to its search endpoint.
        var gbk = Encoding.GetEncoding("gbk");
        var bytes = gbk.GetBytes(query);
        // Percent-encode non-ASCII bytes
        var encodedQuery = string.Concat(bytes.Select(b =>
            b < 128 ? ((char)b).ToString() : $"%{b:X2}"));
        string body = $"searchkey={encodedQuery}";
        return (body, "gb2312");
    }

    public ListingPage ParseListing(string html, string pageUrl)
    {
        var novels = new List<NovelEntry>();
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pattern 1: structured list items — <li> containing links to books
        var blockPattern = new Regex(
            @"<li[^>]*>([\s\S]*?)</li>",
            RegexOptions.IgnoreCase);

        foreach (Match block in blockPattern.Matches(html))
        {
            string content = block.Groups[1].Value;

            var urlM = Regex.Match(content,
                @"href=[""'](?:https?://(?:www\.)?situu\.cc)?/(\d+_\d+)/?[""']",
                RegexOptions.IgnoreCase);
            if (!urlM.Success) continue;

            string bookId = urlM.Groups[1].Value;
            if (!seen.Add(bookId)) continue;

            string url = $"https://www.situu.cc/{bookId}/";

            // Title
            string title = "";
            var titleM = Regex.Match(content, @"title=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            if (titleM.Success) title = System.Net.WebUtility.HtmlDecode(titleM.Groups[1].Value.Trim());
            if (string.IsNullOrWhiteSpace(title))
            {
                var altM = Regex.Match(content, @"alt=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                if (altM.Success) title = System.Net.WebUtility.HtmlDecode(altM.Groups[1].Value.Trim());
            }
            if (string.IsNullOrWhiteSpace(title))
            {
                var h2M = Regex.Match(content, @"<h2[^>]*>([^<]+)</h2>", RegexOptions.IgnoreCase);
                if (h2M.Success) title = System.Net.WebUtility.HtmlDecode(h2M.Groups[1].Value.Trim());
            }
            if (string.IsNullOrWhiteSpace(title))
            {
                var aTextM = Regex.Match(content, @"<a[^>]*>([^<]+)</a>", RegexOptions.IgnoreCase);
                if (aTextM.Success) title = System.Net.WebUtility.HtmlDecode(aTextM.Groups[1].Value.Trim());
            }
            if (string.IsNullOrWhiteSpace(title)) continue;

            title = Regex.Replace(title, @"\s*(最新章节|无弹窗|全文阅读|免费阅读).*$", "").Trim();

            // Cover
            string? cover = null;
            var imgM = Regex.Match(content, @"<img[^>]+src=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            if (imgM.Success) cover = imgM.Groups[1].Value.Trim();
            if (cover != null && !cover.StartsWith("http"))
            {
                cover = new Uri(new Uri(pageUrl), cover).ToString();
            }
            if (cover == null && bookId.Contains('_'))
            {
                var parts = bookId.Split('_');
                if (parts.Length == 2)
                {
                    cover = $"https://www.situu.cc/files/article/image/{parts[0]}/{parts[1]}/{parts[1]}s.jpg";
                }
            }

            // Author
            string? author = null;
            var authM = Regex.Match(content, @"class=[""']pop-intro[""'][^>]*>([^<]+)</span>", RegexOptions.IgnoreCase);
            if (!authM.Success)
            {
                authM = Regex.Match(content, @"<span class=""pop-intro"">([^<]+)</span>", RegexOptions.IgnoreCase);
            }
            if (authM.Success)
            {
                string rawIntro = System.Net.WebUtility.HtmlDecode(authM.Groups[1].Value.Trim());
                if (rawIntro.Contains('/'))
                {
                    author = rawIntro.Split('/').Last().Trim();
                }
                else
                {
                    author = rawIntro;
                }
            }
            if (string.IsNullOrWhiteSpace(author))
            {
                var authText = Regex.Match(content, @"(?:作者|intro)[：:]\s*([^\s<,，\n]+)", RegexOptions.IgnoreCase);
                if (authText.Success) author = authText.Groups[1].Value.Trim();
            }

            // Description
            string? desc = null;
            var descM = Regex.Match(content, @"<p[^>]*class=""[^""]*(?:desc|intro|summary)[^""]*""[^>]*>([^<]{10,})</p>", RegexOptions.IgnoreCase);
            if (descM.Success) desc = System.Net.WebUtility.HtmlDecode(descM.Groups[1].Value.Trim());

            var chMeta = ExtractChapterMeta(content);
            novels.Add(new NovelEntry(title, author, url, cover, desc, null, chMeta.count, chMeta.text));
        }

        // Fallback: direct /bookId/ link scan (useful for basic list rendering and search result fallback)
        if (novels.Count == 0)
        {
            var linkPattern = new Regex(
                @"href=[""'](?:https?://(?:www\.)?situu\.cc)?/(\d+_\d+)/?[""'][^>]*>\s*([^<]{2,80})\s*</a>",
                RegexOptions.IgnoreCase);
            foreach (Match m in linkPattern.Matches(html))
            {
                string bookId = m.Groups[1].Value;
                if (!seen.Add(bookId)) continue;
                string title = System.Net.WebUtility.HtmlDecode(m.Groups[2].Value.Trim());
                if (title.Length < 2) continue;
                string url = $"https://www.situu.cc/{bookId}/";
                string? cover = null;
                var parts = bookId.Split('_');
                if (parts.Length == 2)
                {
                    cover = $"https://www.situu.cc/files/article/image/{parts[0]}/{parts[1]}/{parts[1]}s.jpg";
                }
                novels.Add(new NovelEntry(title, null, url, cover, null, null));
            }
        }

        // Pagination
        bool hasNext = html.Contains("下一页") || html.Contains("Next");
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
