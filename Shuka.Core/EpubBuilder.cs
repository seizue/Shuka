using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Shuka.Core;

public static class EpubBuilder
{
    public static void Build(string path, string titleZh, string titleEn, string authorZh, string authorEn,
        List<(int Idx, string ChTitle, string Text)> chapters, byte[]? coverBytes, string coverMime)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        var mte = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using (var w = new StreamWriter(mte.Open())) w.Write("application/epub+zip");

        WriteEntry(zip, "META-INF/container.xml",
            "<?xml version=\"1.0\"?>" +
            "<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">" +
            "<rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/>" +
            "</rootfiles></container>");

        string uid = $"shuka-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        var items = new List<(string Id, string Fname, string Label)>();

        string coverItemId   = "cover-image";
        string coverExt      = coverMime == "image/png" ? "png" : coverMime == "image/gif" ? "gif" : "jpg";
        string coverImgFname = $"cover.{coverExt}";
        string coverMimeAttr = coverMime;

        if (coverBytes != null)
        {
            var ce = zip.CreateEntry($"OEBPS/{coverImgFname}", CompressionLevel.NoCompression);
            using var cs = ce.Open();
            cs.Write(coverBytes, 0, coverBytes.Length);
        }
        else
        {
            coverImgFname = "cover.svg";
            coverMimeAttr = "image/svg+xml";
            WriteEntry(zip, "OEBPS/cover.svg", GenerateCoverSvg(titleEn, titleZh, authorEn, authorZh));
        }

        WriteEntry(zip, "OEBPS/cover.xhtml",
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.1//EN\" \"http://www.w3.org/TR/xhtml11/DTD/xhtml11.dtd\">" +
            "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>Cover</title>" +
            "<style>body{margin:0;padding:0;text-align:center;background:#000;}" +
            "img{max-width:100%;max-height:100vh;}</style>" +
            "</head><body>" +
            $"<img src=\"{coverImgFname}\" alt=\"Cover\"/>" +
            "</body></html>");
        items.Add(("cover", "cover.xhtml", "Cover"));

        WriteEntry(zip, "OEBPS/titlepage.xhtml",
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.1//EN\" \"http://www.w3.org/TR/xhtml11/DTD/xhtml11.dtd\">" +
            "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>Title Page</title>" +
            "<style>" +
            "body{font-family:Georgia,serif;text-align:center;margin:3em 2em;}" +
            ".title-en{font-size:1.8em;font-weight:bold;margin-bottom:0.3em;}" +
            ".title-zh{font-size:1.3em;color:#555;margin-bottom:1.5em;}" +
            ".author-label{font-size:0.9em;color:#888;text-transform:uppercase;letter-spacing:0.1em;}" +
            ".author-en{font-size:1.2em;font-weight:bold;margin-top:0.2em;}" +
            ".author-zh{font-size:1em;color:#555;}" +
            ".divider{margin:2em auto;width:60px;border-top:2px solid #ccc;}" +
            ".source{font-size:0.8em;color:#aaa;margin-top:3em;}" +
            "</style></head><body>" +
            $"<div class=\"title-en\">{Escape(titleEn)}</div>" +
            $"<div class=\"title-zh\">{Escape(titleZh)}</div>" +
            "<div class=\"divider\"></div>" +
            "<div class=\"author-label\">Author</div>" +
            $"<div class=\"author-en\">{Escape(authorEn)}</div>" +
            $"<div class=\"author-zh\">{Escape(authorZh)}</div>" +
            "<div class=\"source\">Translated to English by Shuka</div>" +
            "</body></html>");
        items.Add(("titlepage", "titlepage.xhtml", "Title Page"));

        foreach (var (idx, chTitle, text) in chapters)
        {
            string id = $"ch{idx}", fname = $"ch{idx}.xhtml";
            items.Add((id, fname, chTitle));
            var paras = text.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l))
                            .Select(l => $"<p>{Escape(l.Trim())}</p>");
            WriteEntry(zip, $"OEBPS/{fname}",
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.1//EN\" \"http://www.w3.org/TR/xhtml11/DTD/xhtml11.dtd\">" +
                "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head>" +
                $"<title>{Escape(chTitle)}</title>" +
                "<style>body{font-family:Georgia,serif;line-height:1.8;margin:2em}p{margin:0.5em 0}</style>" +
                $"</head><body><h2>{Escape(chTitle)}</h2>" +
                string.Join("", paras) + "</body></html>");
        }

        string coverItem = $"<item id=\"{coverItemId}\" href=\"{coverImgFname}\" media-type=\"{coverMimeAttr}\" properties=\"cover-image\"/>";
        string mf = coverItem + string.Join("", items.Select(c => $"<item id=\"{c.Id}\" href=\"{c.Fname}\" media-type=\"application/xhtml+xml\"/>"));
        string sp = string.Join("", items.Select(c => $"<itemref idref=\"{c.Id}\"/>"));
        string np = string.Join("", items.Select((c, i) =>
            $"<navPoint id=\"n{i+1}\" playOrder=\"{i+1}\"><navLabel><text>{Escape(c.Label)}</text></navLabel><content src=\"{c.Fname}\"/></navPoint>"));

        WriteEntry(zip, "OEBPS/content.opf",
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<package xmlns=\"http://www.idpf.org/2007/opf\" unique-identifier=\"uid\" version=\"2.0\">" +
            "<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">" +
            $"<dc:title>{Escape(titleEn)}</dc:title><dc:creator>{Escape(authorEn)}</dc:creator>" +
            $"<dc:language>en</dc:language><dc:identifier id=\"uid\">{uid}</dc:identifier>" +
            "<meta name=\"cover\" content=\"cover-image\"/>" +
            $"</metadata><manifest><item id=\"ncx\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/>{mf}</manifest>" +
            $"<spine toc=\"ncx\">{sp}</spine></package>");

        WriteEntry(zip, "OEBPS/toc.ncx",
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<!DOCTYPE ncx PUBLIC \"-//NISO//DTD ncx 2005-1//EN\" \"http://www.daisy.org/z3986/2005/ncx-2005-1.dtd\">" +
            "<ncx xmlns=\"http://www.daisy.org/z3986/2005/ncx/\" version=\"2005-1\">" +
            $"<head><meta name=\"dtb:uid\" content=\"{uid}\"/></head>" +
            $"<docTitle><text>{Escape(titleEn)}</text></docTitle><navMap>{np}</navMap></ncx>");
    }

    public static string GenerateCoverSvg(string titleEn, string titleZh, string authorEn, string authorZh)
    {
        var palettes = new[]
        {
            ("#1a1a2e", "#e94560", "#ffffff"),
            ("#0f3460", "#e94560", "#ffffff"),
            ("#16213e", "#0f3460", "#e2b96f"),
            ("#2d1b69", "#11998e", "#ffffff"),
            ("#1a1a1a", "#c0392b", "#f5f5f5"),
            ("#0d2137", "#f7971e", "#ffffff"),
        };
        int pick = Math.Abs(titleEn.GetHashCode()) % palettes.Length;
        var (bg, accent, fg) = palettes[pick];

        var titleLines = WrapText(titleEn, 22);
        var titleSvg = string.Join("", titleLines.Select((l, i) =>
            $"<tspan x=\"300\" dy=\"{(i == 0 ? 0 : 52)}\">{Escape(l)}</tspan>"));

        double titleBlockH = titleLines.Count * 52;
        double titleY = 280 - titleBlockH / 2;
        string zhShort = titleZh.Length > 20 ? titleZh[..20] + "…" : titleZh;

        return
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"600\" height=\"900\" viewBox=\"0 0 600 900\">" +
            $"<rect width=\"600\" height=\"900\" fill=\"{bg}\"/>" +
            $"<rect x=\"0\" y=\"0\" width=\"600\" height=\"8\" fill=\"{accent}\"/>" +
            $"<rect x=\"60\" y=\"60\" width=\"480\" height=\"2\" fill=\"{accent}\" opacity=\"0.4\"/>" +
            $"<rect x=\"60\" y=\"840\" width=\"480\" height=\"2\" fill=\"{accent}\" opacity=\"0.4\"/>" +
            $"<circle cx=\"300\" cy=\"450\" r=\"260\" fill=\"none\" stroke=\"{accent}\" stroke-width=\"1\" opacity=\"0.15\"/>" +
            $"<circle cx=\"300\" cy=\"450\" r=\"200\" fill=\"none\" stroke=\"{accent}\" stroke-width=\"1\" opacity=\"0.1\"/>" +
            $"<rect x=\"60\" y=\"{titleY - 30}\" width=\"480\" height=\"{titleBlockH + 60}\" fill=\"{accent}\" opacity=\"0.08\" rx=\"4\"/>" +
            $"<rect x=\"60\" y=\"{titleY - 30}\" width=\"4\" height=\"{titleBlockH + 60}\" fill=\"{accent}\" rx=\"2\"/>" +
            $"<text x=\"300\" y=\"{titleY}\" font-family=\"Georgia, serif\" font-size=\"44\" font-weight=\"bold\" " +
            $"fill=\"{fg}\" text-anchor=\"middle\">{titleSvg}</text>" +
            $"<text x=\"300\" y=\"{titleY + titleBlockH + 24}\" font-family=\"serif\" font-size=\"22\" " +
            $"fill=\"{fg}\" text-anchor=\"middle\" opacity=\"0.6\">{Escape(zhShort)}</text>" +
            $"<line x1=\"220\" y1=\"580\" x2=\"380\" y2=\"580\" stroke=\"{accent}\" stroke-width=\"1.5\"/>" +
            $"<text x=\"300\" y=\"610\" font-family=\"Georgia, serif\" font-size=\"24\" font-weight=\"bold\" " +
            $"fill=\"{fg}\" text-anchor=\"middle\">{Escape(authorEn)}</text>" +
            $"<text x=\"300\" y=\"645\" font-family=\"serif\" font-size=\"18\" " +
            $"fill=\"{fg}\" text-anchor=\"middle\" opacity=\"0.6\">{Escape(authorZh)}</text>" +
            $"<text x=\"300\" y=\"870\" font-family=\"Georgia, serif\" font-size=\"13\" " +
            $"fill=\"{fg}\" text-anchor=\"middle\" opacity=\"0.35\">Translated to English by Shuka</text>" +
            "</svg>";
    }

    private static List<string> WrapText(string text, int maxChars)
    {
        var words = text.Split(' ');
        var lines = new List<string>();
        var cur   = new StringBuilder();
        foreach (var word in words)
        {
            if (cur.Length + word.Length + 1 > maxChars && cur.Length > 0)
            { lines.Add(cur.ToString()); cur.Clear(); }
            if (cur.Length > 0) cur.Append(' ');
            cur.Append(word);
        }
        if (cur.Length > 0) lines.Add(cur.ToString());
        return lines.Count > 0 ? lines : new List<string> { text };
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    public static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
