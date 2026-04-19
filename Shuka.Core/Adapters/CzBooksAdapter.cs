using System.Text.RegularExpressions;

namespace Shuka.Core.Adapters;

// czbooks.net adapter
public class CzBooksAdapter : ISiteAdapter
{
    public string SiteName => "czbooks.net";

    public bool Matches(string url) =>
        url.Contains("czbooks.net", StringComparison.OrdinalIgnoreCase);

    public string NormalizeUrl(string url)
    {
        if (!url.StartsWith("http")) url = "https://" + url;
        var m = Regex.Match(url, @"(https?://czbooks\.net/n/[^/]+)(?:/.*)?$", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : url;
    }

    public IndexInfo ParseIndex(string html, string indexUrl)
    {
        string title = Regex.Match(html, @"《([^》]+)》").Groups[1].Value.Trim();
        if (string.IsNullOrWhiteSpace(title))
            title = Regex.Match(html, @"<title[^>]*>([^<|]+)", RegexOptions.IgnoreCase).Groups[1].Value.Trim();
        if (string.IsNullOrWhiteSpace(title))
            title = Regex.Match(indexUrl, @"/n/([^/]+)").Groups[1].Value;

        string author = "Unknown";
        var am = Regex.Match(html, @"作者[：:\s]*<[^>]+>([^<]+)<", RegexOptions.IgnoreCase);
        if (!am.Success) am = Regex.Match(html, @"作者[：:\s]+([^\s<\n,，]+)");
        if (am.Success) author = am.Groups[1].Value.Trim();

        string bookId = Regex.Match(indexUrl, @"/n/([^/]+)").Groups[1].Value;

        var chapters = Regex.Matches(html,
                @"href=[""'][^""']*(/n/" + Regex.Escape(bookId) + @"/([^""'?]+))\?chapterNumber=(\d+)[""']",
                RegexOptions.IgnoreCase)
            .Cast<Match>()
            .Select(m => new {
                Url  = "https://czbooks.net" + m.Groups[1].Value,
                Code = m.Groups[2].Value,
                Num  = int.Parse(m.Groups[3].Value)
            })
            .DistinctBy(x => x.Code)
            .OrderBy(x => x.Num)
            .Select(x => new ChapterRef(x.Url, $"Chapter {x.Num}"))
            .ToList();

        string? cover = null;
        var og = Regex.Match(html, @"<img[^>]+src=[""'](https?://img\.czbooks\.net/[^""']+)[""']", RegexOptions.IgnoreCase);
        if (og.Success) cover = og.Groups[1].Value;
        if (cover == null)
        {
            var ogm = Regex.Match(html, @"<meta[^>]+property=[""']og:image[""'][^>]+content=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            if (!ogm.Success) ogm = Regex.Match(html, @"<meta[^>]+content=[""']([^""']+)[""'][^>]+property=[""']og:image[""']", RegexOptions.IgnoreCase);
            if (ogm.Success) cover = ogm.Groups[1].Value.Trim();
        }

        return new IndexInfo(title, author, chapters, cover);
    }

    public List<string> ExtractChapterText(string html)
    {
        html = Regex.Replace(html, @"<script[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<style[\s\S]*?</style>",   "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<nav[\s\S]*?</nav>",       "", RegexOptions.IgnoreCase);

        var contentMatch = Regex.Match(html,
            @"<div[^>]+class=[""'][^""']*(?:content|chapter-content|article)[^""']*[""'][^>]*>([\s\S]*?)</div>",
            RegexOptions.IgnoreCase);

        string content = contentMatch.Success ? contentMatch.Groups[1].Value : html;
        content = Regex.Replace(content, @"<[^>]+>", "\n");
        content = content.Replace("&nbsp;", " ").Replace("&amp;", "&")
                         .Replace("&lt;", "<").Replace("&gt;", ">")
                         .Replace("&quot;", "\"").Replace("&#39;", "'");

        var result = new List<string>();
        foreach (var line in content.Split('\n'))
        {
            string trimmed = line.Trim().TrimStart('\u3000').Trim();
            if (trimmed.Length > 0 && Regex.IsMatch(trimmed, @"[\u4e00-\u9fff\u3400-\u4dbf]"))
                result.Add(trimmed);
        }
        return result;
    }
}
