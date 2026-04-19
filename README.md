# Shuka

A Windows tool that downloads Chinese web novels, translates them to English via Google Translate, and saves them as `.epub` files ready for any e-reader.

[![Github Downloads](https://img.shields.io/github/downloads/seizue/Shuka/total?label=Github%20Downloads&logo=github&style=flat)](https://github.com/seizue/Shuka/releases)

## Supported Sites

| Site | Example URL |
|------|-------------|
| [52shuku.net](https://www.52shuku.net) | `https://www.52shuku.net/bl/09_b/bkd7d.html` |
| [czbooks.net](https://czbooks.net) | `https://czbooks.net/n/clgajm` |

> czbooks.net is protected by Cloudflare. Shuka handles this automatically using a headless browser — no extra setup needed.

## Features

- Downloads and translates Chinese novels to English
- Saves output as a properly formatted `.epub` (cover, title page, chapters)
- Auto-detects cover image from the novel's index page
- Generates a styled SVG cover if no image is found
- Single or batch download mode
- Parallel fetch + translate pipeline for faster downloads
- Paste any page URL — Shuka automatically resolves to the correct index
- Extensible adapter system for adding new sites in the future

## Installation

Download and run `Shuka_Setup.exe` from the [Releases](../../releases) page. No admin rights required.

The installer places everything in `%LocalAppData%\Shuka`, creates a Start Menu shortcut, and installs the Chromium browser needed for Cloudflare bypass.

## Usage

Launch **Shuka** from the Start Menu or desktop shortcut:

```
===============================================
       Shuka -> Chinese to English (EPUB)
===============================================

  1. Download single novel
  2. Batch download (multiple novels)
  3. Exit
```

### Single download

Paste the novel URL, optionally provide a cover image URL, and choose how many chapters to download (leave blank for all). The `.epub` is saved to your Downloads folder.

### Batch download

Add novels one by one. For each you can provide an optional cover URL. When done, choose to start — all novels download sequentially, one `.epub` each.

```
--- Novel #1 ---
Novel URL:  https://czbooks.net/n/clgajm
Cover URL:  (optional)
Novel #1 added.

  1. Add another novel
  2. Start downloading (1 queued)
  3. Cancel
```

## Command line

```
# Single novel (all chapters)
Shuka.exe <url>

# Single novel (first 3 chapters, useful for testing)
Shuka.exe <url> 3

# Single novel with a custom cover
Shuka.exe <url> 0 "" <cover-url>

# Batch from a text file (one URL per line, # for comments)
Shuka.exe --batch urls.txt
```

Output is saved to `%USERPROFILE%\Downloads` by default.

## Building from source

Requires [.NET 9 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build -c Release
```

To build the installer, publish first then compile with [Inno Setup](https://jrsoftware.org/isinfo.php):

```bash
dotnet publish -c Release -r win-x64 --self-contained false -o bin/publish
Shuka.exe playwright install chromium
ISCC.exe installer.iss
```

## Adding a new site

Implement `ISiteAdapter` in `Program.cs` and add it to the `adapters` array in `DetectAdapter`:

```csharp
class MySiteAdapter : ISiteAdapter
{
    public string SiteName => "mysite.com";
    public bool Matches(string url) => url.Contains("mysite.com");
    public string NormalizeUrl(string url) => /* strip chapter suffix etc */;
    public IndexInfo ParseIndex(string html, string indexUrl) => /* parse title, author, chapter list */;
    public List<string> ExtractChapterText(string html) => /* extract paragraphs */;
}
```

## Screenshot
<img width="1366" height="736" alt="Shuka" src="https://github.com/user-attachments/assets/b66d8ef3-858b-4ff9-a8a9-981a830a41a3" />



## License

See [LICENSE](LICENSE).
