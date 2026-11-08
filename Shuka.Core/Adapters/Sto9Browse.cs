using System.Text.RegularExpressions;

namespace Shuka.Core.Adapters;

/// <summary>
/// Browse/discover support for sto9.com (思兔閱讀).
///
/// Hot / New: https://sto9.com/novels/hot (page 1) -> https://sto9.com/novels/newhot_0_0_{page}.html
/// Search:    POST to https://sto9.com/search with body "searchkey={query}&amp;searchtype=all"
/// </summary>
public class Sto9Browse : IBrowsableAdapter
{
    public string SiteName         => "sto9.com";
    public string Description      => "思兔閱讀 · Popular & trending novels";
    public string IconGlyph        => "\uE894"; // language (globe)
    public bool   RequiresCfBypass => false;

    public string HomeUrl => "https://sto9.com/novels/hot";

    public string GetRecentUrl(int page = 1) =>
        page == 1 ? "https://sto9.com/novels/hot" : $"https://sto9.com/novels/newhot_0_0_{page}.html";

    public string GetPopularUrl(int page = 1) =>
        page == 1 ? "https://sto9.com/novels/hot" : $"https://sto9.com/novels/newhot_0_0_{page}.html";

    public string GetSearchUrl(string query, int page = 1) =>
        "https://sto9.com/search";

    public (string postBody, string charset)? GetSearchPostBody(string query, int page = 1) =>
        ($"searchkey={Uri.EscapeDataString(query)}&searchtype=all", "utf-8");

    public IReadOnlyList<SourceFilter>? Filters => new SourceFilter[]
    {
        new("Hot & Trending",       page => page == 1 ? "https://sto9.com/novels/hot" : $"https://sto9.com/novels/newhot_0_0_{page}.html"),
        new("Weekly Popular",       page => $"https://sto9.com/novels/weekvisit_0_0_{page}.html"),
        new("Most Recommended",    page => $"https://sto9.com/novels/allvote_0_0_{page}.html"),
        new("Completed",           page => $"https://sto9.com/novels/newhot_0_1_{page}.html"),
        new("Fantasy",             page => $"https://sto9.com/novels/newhot_1_0_{page}.html"),
        new("Wuxia & Xianxia",     page => $"https://sto9.com/novels/newhot_2_0_{page}.html"),
        new("Urban Life",          page => $"https://sto9.com/novels/newhot_3_0_{page}.html"),
        new("History & Military",  page => $"https://sto9.com/novels/newhot_4_0_{page}.html"),
        new("Sci-Fi & Future",     page => $"https://sto9.com/novels/newhot_6_0_{page}.html"),
    };

    public ListingPage ParseListing(string html, string pageUrl)
    {
        var novels = new List<NovelEntry>();
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // sto9.com wraps each book entry in an <li> block containing an imgbox and a newnav
        var blockPattern = new Regex(
            @"<li>\s*<a[^>]*class=""imgbox""[^>]*>[\s\S]*?</li>",
            RegexOptions.IgnoreCase);

        foreach (Match block in blockPattern.Matches(html))
        {
            string content = block.Value;

            var titleM = Regex.Match(content,
                @"<h3><a[^>]*href=[""']([^""']+/book/(\d+)\.html)[""'][^>]*>([^<]+)</a></h3>",
                RegexOptions.IgnoreCase);
            if (!titleM.Success) continue;

            string bookUrl = titleM.Groups[1].Value.Trim();
            string bookId  = titleM.Groups[2].Value.Trim();
            string title   = System.Net.WebUtility.HtmlDecode(titleM.Groups[3].Value.Trim());

            if (!bookUrl.StartsWith("http"))
                bookUrl = "https://sto9.com" + (bookUrl.StartsWith('/') ? bookUrl : "/" + bookUrl);

            if (!seen.Add(bookUrl)) continue;

            // Author & Status from <div class="labelbox">
            string? author = null;
            string? status = null;
            var labelBoxM = Regex.Match(content,
                @"<div class=""labelbox"">([\s\S]*?)</div>",
                RegexOptions.IgnoreCase);
            if (labelBoxM.Success)
            {
                var labels = Regex.Matches(labelBoxM.Groups[1].Value,
                    @"<label>(?:<a[^>]*>)?([^<]+?)(?:</a>)?</label>",
                    RegexOptions.IgnoreCase);
                if (labels.Count > 0) author = System.Net.WebUtility.HtmlDecode(labels[0].Groups[1].Value.Trim());
                if (labels.Count > 1) status = System.Net.WebUtility.HtmlDecode(labels[1].Groups[1].Value.Trim());
            }

            // Cover from data-src (or fallback to src or computed CDN url)
            string? cover = null;
            var coverM = Regex.Match(content,
                @"<img[^>]+(?:data-src|src)=[""']([^""']+)[""']",
                RegexOptions.IgnoreCase);
            if (coverM.Success)
            {
                cover = coverM.Groups[1].Value.Trim();
                if (cover.Contains("nocover") && int.TryParse(bookId, out int bid))
                {
                    int prefix = bid / 1000;
                    cover = $"https://sto9.com/files/article/image/{prefix}/{bookId}/{bookId}s.jpg";
                }
                else if (!cover.StartsWith("http"))
                {
                    cover = "https://sto9.com" + (cover.StartsWith('/') ? cover : "/" + cover);
                }
            }

            // Description / synopsis from <ol class="ellipsis_2">
            string? desc = null;
            var descM = Regex.Match(content,
                @"<ol[^>]*class=""[^""]*ellipsis[^""]*""[^>]*>([\s\S]*?)</ol>",
                RegexOptions.IgnoreCase);
            if (descM.Success)
            {
                desc = System.Net.WebUtility.HtmlDecode(
                    Regex.Replace(descM.Groups[1].Value, @"<[^>]+>", " ").Trim());
            }

            // Latest chapter
            string? latestChapter = null;
            var chapterM = Regex.Match(content,
                @"<div class=""zxzj"">[\s\S]*?<a[^>]*>([^<]+)</a>",
                RegexOptions.IgnoreCase);
            if (chapterM.Success)
                latestChapter = System.Net.WebUtility.HtmlDecode(chapterM.Groups[1].Value.Trim());

            novels.Add(new NovelEntry(
                Title:        title,
                Author:       author,
                Url:          bookUrl,
                CoverUrl:     cover,
                Description:  desc,
                Tags:         status,
                ChapterText:  latestChapter
            ));
        }

        // Pagination: page has "class="next"" link
        bool hasNext = html.Contains("class=\"next\"") || html.Contains("class='next'");

        int pageNum = 1;
        var pageM = Regex.Match(pageUrl, @"_(\d+)\.html", RegexOptions.IgnoreCase);
        if (pageM.Success) int.TryParse(pageM.Groups[1].Value, out pageNum);

        return new ListingPage(novels, hasNext, pageNum);
    }
}

