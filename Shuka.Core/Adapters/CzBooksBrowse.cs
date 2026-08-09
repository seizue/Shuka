using System.Text.RegularExpressions;

namespace Shuka.Core.Adapters;

/// <summary>
/// Browse/discover support for czbooks.net.
///
/// czbooks.net is behind Cloudflare and blocks all HTTP fetches (403).
/// It works fine in a real browser/WebView. The URLs below are used by
/// WebBrowsePage to open the site — ParseListing is a best-effort fallback
/// but will typically return empty (the WebView handles browsing directly).
///
/// Recent:  https://czbooks.net/new/1
/// Popular: https://czbooks.net/hot/1
/// Search:  https://czbooks.net/search?q={query}
/// </summary>
public class CzBooksBrowse : IBrowsableAdapter
{
    public string SiteName => "czbooks.net";
    public string Description => "Chinese novels · Cloudflare protected";
    public string IconGlyph => "\uE894"; // language (globe)
    public bool RequiresCfBypass => true;

    public string HomeUrl => "https://czbooks.net/c/baihe";

    public string GetRecentUrl(int page = 1) =>
        page == 1 ? "https://czbooks.net/c/baihe" : $"https://czbooks.net/c/baihe/{page}";
    public string GetPopularUrl(int page = 1) =>
        page == 1 ? "https://czbooks.net/c/baihe" : $"https://czbooks.net/c/baihe/{page}";
    public string GetSearchUrl(string query, int page = 1) =>
        $"https://czbooks.net/s/{Uri.EscapeDataString(query)}/{page}";

    public ListingPage ParseListing(string html, string pageUrl)
    {
        var novels = new List<NovelEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // czbooks book links: /n/{bookId}  (no chapter sub-path)
        var linkPattern = new Regex(
            @"href=[""'](?:https?:)?(?://czbooks\.net)?/n/([a-zA-Z0-9]+)(?:/[^""'#\s]*|\?[^""'#\s]*)?[""']",
            RegexOptions.IgnoreCase);

        foreach (Match m in linkPattern.Matches(html))
        {
            string bookId = m.Groups[1].Value;
            if (bookId.Length < 3) continue; // skip noise
            if (string.Equals(bookId, "info", StringComparison.OrdinalIgnoreCase)) continue;

            // Skip chapter links (e.g. /n/bookId/chapterId)
            string fullHref = m.Value;
            if (Regex.IsMatch(fullHref, @"/n/[a-zA-Z0-9]+/[a-zA-Z0-9]+", RegexOptions.IgnoreCase))
                continue;

            if (!seen.Add(bookId)) continue;

            // Grab context around this link for metadata extraction
            int start = Math.Max(0, m.Index - 400);
            int end   = Math.Min(html.Length, m.Index + 800);
            string ctx = html.Substring(start, end - start);

            // ── Title ─────────────────────────────────────────────────────────────
            string title = "";
            var titleM = Regex.Match(ctx,
                @"(?:novel-item-title|class=[""'][^""']*\btitle\b[^""']*[""'])[^>]*>\s*([^<]{2,120})",
                RegexOptions.IgnoreCase);
            if (titleM.Success)
                title = System.Net.WebUtility.HtmlDecode(titleM.Groups[1].Value.Trim());

            if (string.IsNullOrWhiteSpace(title))
            {
                var hM = Regex.Match(ctx,
                    @"<h[1-6][^>]*>\s*([^<]{2,120})\s*</h[1-6]>", RegexOptions.IgnoreCase);
                if (hM.Success) title = System.Net.WebUtility.HtmlDecode(hM.Groups[1].Value.Trim());
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                var aM = Regex.Match(ctx,
                    @"href=[""'](?:https?:)?(?://czbooks\.net)?/n/" + Regex.Escape(bookId) + @"(?:/[^""'#\s]*|\?[^""'#\s]*)?[""'][^>]*>\s*([^<]{2,120}?)\s*</a>",
                    RegexOptions.IgnoreCase);
                if (aM.Success) title = System.Net.WebUtility.HtmlDecode(aM.Groups[1].Value.Trim());
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                var altM = Regex.Match(ctx, @"<img[^>]+alt=[""']([^""']{2,120})[""']", RegexOptions.IgnoreCase);
                if (altM.Success) title = System.Net.WebUtility.HtmlDecode(altM.Groups[1].Value.Trim());
            }

            if (string.IsNullOrWhiteSpace(title)) continue;

            // ── Author ────────────────────────────────────────────────────────────
            string? author = null;
            var authM = Regex.Match(ctx,
                @"(?:novel-item-author|author)[^>]*>(?:[\s\S]{0,60}?<a[^>]*>([^<]+)</a>|\s*([^<]{1,40}))",
                RegexOptions.IgnoreCase);
            if (authM.Success)
            {
                string val = authM.Groups[1].Success ? authM.Groups[1].Value : authM.Groups[2].Value;
                author = System.Net.WebUtility.HtmlDecode(val.Trim());
            }
            if (string.IsNullOrWhiteSpace(author))
            {
                authM = Regex.Match(ctx, @"作者[：:\s]*([^\s<,，\n]{1,30})");
                if (authM.Success) author = authM.Groups[1].Value.Trim();
            }

            // ── Cover ─────────────────────────────────────────────────────────────
            string? cover = null;
            var imgM = Regex.Match(ctx,
                @"(?:src|data-src|srcset)=[""']?((?:https?:)?//[^""'\s>]+\.(?:jpg|jpeg|png|webp))", RegexOptions.IgnoreCase);
            if (imgM.Success)
            {
                cover = imgM.Groups[1].Value;
                if (cover.StartsWith("//")) cover = "https:" + cover;
            }

            var chapterMeta = ExtractChapterMeta(ctx);
            novels.Add(new NovelEntry(
                title, author,
                $"https://czbooks.net/n/{bookId}",
                cover, null, null,
                chapterMeta.count, chapterMeta.text));
        }

        bool hasNext = html.Contains("下一頁") || html.Contains("下一页") ||
                       html.Contains("pagination") ||
                       Regex.IsMatch(html, @"/c/baihe/\d+", RegexOptions.IgnoreCase) ||
                       Regex.IsMatch(html, @"/(?:new|hot)/\d+", RegexOptions.IgnoreCase);
        int currentPage = 1;
        var pageM = Regex.Match(pageUrl, @"/c/baihe/(\d+)$", RegexOptions.IgnoreCase);
        if (!pageM.Success) pageM = Regex.Match(pageUrl, @"/(?:new|hot|s/[^/]+)/(\d+)$", RegexOptions.IgnoreCase);
        if (!pageM.Success) pageM = Regex.Match(pageUrl, @"/(\d+)$");
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

        var en = Regex.Match(sample, @"\b([0-9]{1,5})\s*chapters?\b", RegexOptions.IgnoreCase);
        if (en.Success && int.TryParse(en.Groups[1].Value, out int enCount))
            return (enCount, $"{enCount} chapters");

        return (null, null);
    }
}
