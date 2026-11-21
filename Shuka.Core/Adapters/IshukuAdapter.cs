using System.Text.Json;
using System.Text.RegularExpressions;

namespace Shuka.Core.Adapters;

/// <summary>
/// Adapter for ishuku.net (爱书库) — Simplified Chinese novel site.
///
/// Index URL format: https://ishuku.net/books/{bookId}
/// Chapter list API: https://ishuku.net/api/books/{bookId}/chapters   (JSON array)
/// Chapter content API: https://ishuku.net/api/books/{bookId}/chapters/{chapterId}  (JSON)
/// No Cloudflare protection. UTF-8 encoding.
/// </summary>
public class IshukuAdapter : ISiteAdapter
{
    public string SiteName => "ishuku.net";
    public bool RequiresCfBypass => false;

    public bool Matches(string url) =>
        url.Contains("ishuku.net", StringComparison.OrdinalIgnoreCase);

    public string NormalizeUrl(string url)
    {
        if (!url.StartsWith("http")) url = "https://" + url;
        var m = Regex.Match(url, @"(https?://ishuku\.net/books/[^/?#]+)");
        if (m.Success) return m.Groups[1].Value;
        return url;
    }

    public IndexInfo ParseIndex(string html, string indexUrl)
    {
        string bookId = Regex.Match(indexUrl, @"/books/([^/?#]+)").Groups[1].Value;

        string cleanHtml = Regex.Replace(html, @"<script[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
        cleanHtml = Regex.Replace(cleanHtml, @"<style[\s\S]*?</style>", "", RegexOptions.IgnoreCase);

        // Title: try <h1> first
        string title = Regex.Match(cleanHtml, @"<h1[^>]*>\s*([^<]+?)\s*</h1>", RegexOptions.IgnoreCase).Groups[1].Value.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            var ogT = Regex.Match(html, @"<meta[^>]+property=[""']og:title[""'][^>]+content=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            if (!ogT.Success)
                ogT = Regex.Match(html, @"<meta[^>]+content=[""']([^""']+)[""'][^>]+property=[""']og:title[""']", RegexOptions.IgnoreCase);
            if (ogT.Success) title = ogT.Groups[1].Value.Trim();
        }
        if (string.IsNullOrWhiteSpace(title))
            title = bookId;

        // Author
        string author = "Unknown";
        var am = Regex.Match(cleanHtml, @"作者[：:]\s*<[^>]+>([^<]+)<", RegexOptions.IgnoreCase);
        if (!am.Success) am = Regex.Match(cleanHtml, @"作者[：:]\s*([^\s<\n,，]+)");
        if (am.Success) author = am.Groups[1].Value.Trim();

        // Cover: construct directly from bookId — the SPA page has no server-rendered og:image
        string? cover = string.IsNullOrEmpty(bookId) ? null : $"https://ishuku.net/covers/{bookId}";

        // No chapters from HTML — API returns them
        string chapterListUrl = $"https://ishuku.net/api/books/{bookId}/chapters";
        return new IndexInfo(title, author, new List<ChapterRef>(), cover, ChapterListUrl: chapterListUrl);
    }

    public List<ChapterRef> ParseChapterList(string json, string listUrl)
    {
        string bookId = Regex.Match(listUrl, @"/api/books/([^/]+)/chapters").Groups[1].Value;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var arr = doc.RootElement;
            if (arr.ValueKind != JsonValueKind.Array) return new List<ChapterRef>();

            var chapters = new List<ChapterRef>();
            foreach (var el in arr.EnumerateArray())
            {
                string chapterId = el.TryGetProperty("id", out var idProp) ? idProp.GetRawText().Trim('"') : "";
                string chTitle = el.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(chapterId)) continue;
                if (string.IsNullOrWhiteSpace(chTitle)) chTitle = $"Chapter {chapters.Count + 1}";
                string url = $"https://ishuku.net/api/books/{bookId}/chapters/{chapterId}";
                chapters.Add(new ChapterRef(url, chTitle));
            }
            return chapters;
        }
        catch
        {
            return new List<ChapterRef>();
        }
    }

    public List<string> ExtractChapterText(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();

        string contentHtml;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("content_html", out var htmlProp))
                return new List<string>();
            contentHtml = htmlProp.GetString() ?? "";
        }
        catch
        {
            return new List<string>();
        }

        if (string.IsNullOrWhiteSpace(contentHtml)) return new List<string>();

        // Convert <p> and <br> to newlines, strip remaining tags, decode entities
        string text = Regex.Replace(contentHtml, @"<p[^>]*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</p>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", "");
        text = System.Net.WebUtility.HtmlDecode(text);

        var result = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            string trimmed = line.Trim('\r', ' ', '\t', '\u3000', '\u00A0', '\u2002', '\u2003', '\u2009', '\uFEFF');
            if (trimmed.Length == 0) continue;

            // Keep lines with CJK characters or Chinese dialogue/punctuation
            if (Regex.IsMatch(trimmed, @"[\u4e00-\u9fff\u3400-\u4dbf\uf900-\ufaff]") ||
                (trimmed.Length >= 2 && Regex.IsMatch(trimmed, @"^[""""'''（(].*[""""'''）)…?!？！~]$")))
            {
                result.Add(trimmed);
            }
        }
        return result;
    }
}
