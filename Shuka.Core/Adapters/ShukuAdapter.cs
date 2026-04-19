using System.Text.RegularExpressions;

namespace Shuka.Core.Adapters;

// 52shuku.net adapter
public class ShukuAdapter : ISiteAdapter
{
    public string SiteName => "52shuku.net";

    public bool Matches(string url) =>
        url.Contains("52shuku.net", StringComparison.OrdinalIgnoreCase);

    public string NormalizeUrl(string url)
    {
        // Strip chapter suffix: bkd7d_2.html -> bkd7d.html
        url = Regex.Replace(url, @"_\d+\.html$", ".html");
        if (url.StartsWith("http://")) url = "https://" + url[7..];
        if (!url.StartsWith("http")) url = "https://" + url;
        return url;
    }

    public IndexInfo ParseIndex(string html, string indexUrl)
    {
        string title = Regex.Match(html, @"<h1[^>]*>([\s\S]*?)</h1>", RegexOptions.IgnoreCase).Groups[1].Value;
        title = Regex.Replace(title, @"<[^>]+>", "").Trim();
        title = Regex.Replace(title, @"\s*\(\d+\)\s*$", "").Trim();
        if (string.IsNullOrWhiteSpace(title))
            title = Regex.Match(indexUrl, @"/([^/]+)\.html$").Groups[1].Value;

        string author = "Unknown";
        var am = Regex.Match(html, @"作者[：:]\s*([^\s【\n】<&]+)");
        if (am.Success) author = am.Groups[1].Value.Trim();

        string baseUrl = Regex.Replace(indexUrl, @"\.html$", "");
        var chapterUrls = Regex.Matches(html, @"href=[""']?([^""'\s>]+)[""']?", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value)
            .Select(h => h.StartsWith("http") ? h : new Uri(new Uri(indexUrl), h).ToString())
            .Where(u => u.StartsWith(baseUrl + "_") && u.EndsWith(".html"))
            .Distinct()
            .OrderBy(u => { var m = Regex.Match(u, @"_(\d+)\.html$"); return m.Success ? int.Parse(m.Groups[1].Value) : 0; })
            .Select((u, i) => new ChapterRef(u, $"Page {i + 1}"))
            .ToList();

        string? cover = null;
        var og = Regex.Match(html, @"<meta[^>]+property=[""']og:image[""'][^>]+content=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        if (!og.Success) og = Regex.Match(html, @"<meta[^>]+content=[""']([^""']+)[""'][^>]+property=[""']og:image[""']", RegexOptions.IgnoreCase);
        if (og.Success) cover = og.Groups[1].Value.Trim();

        return new IndexInfo(title, author, chapterUrls, cover);
    }

    public List<string> ExtractChapterText(string html)
    {
        html = Regex.Replace(html, @"<script[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<style[\s\S]*?</style>",   "", RegexOptions.IgnoreCase);
        var result = new List<string>();
        foreach (Match m in Regex.Matches(html, @"<p(?:\s[^>]*)?>([^<]*(?:<(?!/p>)[^<]*)*)</p>", RegexOptions.IgnoreCase))
        {
            string inner = m.Groups[1].Value;
            inner = Regex.Replace(inner, @"<[^>]+>", "");
            inner = inner.Replace("&nbsp;", " ").Replace("&amp;", "&")
                         .Replace("&lt;", "<").Replace("&gt;", ">")
                         .Replace("&quot;", "\"").Replace("&#39;", "'")
                         .Replace("\u3000", " ").Trim();
            if (inner.Length > 0 && Regex.IsMatch(inner, @"[\u4e00-\u9fff\u3400-\u4dbf]"))
                result.Add(inner);
        }
        return result;
    }
}
