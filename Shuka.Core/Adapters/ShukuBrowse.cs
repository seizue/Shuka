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

        // Each novel entry on 52shuku is structured as:
        //   ## [Title_Author【status】](https://www.52shuku.net/{cat}/{folder}/bk{id}.html)
        //   　　《Title》作者：Author【status】　　简介：　　Description...
        //   ( date )
        //
        // The heading link contains both title and author separated by underscore.

        var entryPattern = new Regex(
            @"##\s*\[([^\]]+)\]\((https?://(?:www\.)?52shuku\.net/[^)]+\.html)\)",
            RegexOptions.IgnoreCase);

        foreach (Match m in entryPattern.Matches(html))
        {
            string headingText = m.Groups[1].Value.Trim();
            string url         = m.Groups[2].Value.Trim();

            // Derive a unique key from the URL path
            string urlKey = url;
            if (!seen.Add(urlKey)) continue;

            // Heading format: "Title_Author【status】" or "Title_Author[status]"
            // Strip status suffix like 【完结】【完结+番外】
            string cleanHeading = Regex.Replace(headingText, @"[【\[【][^】\]]*[】\]]", "").Trim();
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

            if (string.IsNullOrWhiteSpace(title) || title.Length < 2) continue;

            // Extract description from the block following the heading
            // Look for 简介：text after the heading match
            string? desc = null;
            int blockStart = m.Index + m.Length;
            int blockEnd   = Math.Min(html.Length, blockStart + 800);
            string block   = html.Substring(blockStart, blockEnd - blockStart);

            var descM = Regex.Match(block, @"简介[：:]\s*　*([^（\(]{10,200})");
            if (descM.Success)
                desc = System.Net.WebUtility.HtmlDecode(descM.Groups[1].Value.Trim());

            // No cover images on this site
            var chMeta = ExtractChapterMeta(block + " " + (desc ?? ""));
            novels.Add(new NovelEntry(title, author, url, null, desc, null, chMeta.count, chMeta.text));
        }

        // Fallback: scan for any 52shuku book links with adjacent text
        if (novels.Count == 0)
        {
            var linkPattern = new Regex(
                @"href=[""'](https?://(?:www\.)?52shuku\.net/[^""']+/bk[^""']+\.html)[""'][^>]*>\s*([^<]{2,80})\s*</a>",
                RegexOptions.IgnoreCase);
            foreach (Match m in linkPattern.Matches(html))
            {
                string url = m.Groups[1].Value;
                if (!seen.Add(url)) continue;
                string rawTitle = System.Net.WebUtility.HtmlDecode(m.Groups[2].Value.Trim());
                // Strip status tags
                string title = Regex.Replace(rawTitle, @"[【\[][^】\]]*[】\]]", "").Trim();
                if (title.Length < 2) continue;
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
        bool hasNext = html.Contains("下一页");
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
