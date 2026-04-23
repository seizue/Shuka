# Shuka

A tool that downloads Chinese web novels, translates them to English via Google Translate, and saves them as `.epub` files ready for any e-reader. Available on **Windows** and **Android**.

![Github Downloads](https://img.shields.io/github/downloads/seizue/Shuka/total?cacheSeconds=60)


## Screenshot

<img width="1366" height="736" alt="Shuka" src="https://github.com/user-attachments/assets/83cb7f5b-fa75-4038-97f3-fce2f3578894" />



### Supported Sites

| Site | Example URL |
|------|------------|
| [52shuku.net](https://www.52shuku.net) | `https://www.52shuku.net/bl/09_b/bkd7d.html` |
| [czbooks.net](https://czbooks.net) | `https://czbooks.net/n/clgajm` |
| [dmxs.org](https://www.dmxs.org) | `https://www.dmxs.org/gdjk/22982.html` |

> **czbooks.net** is protected by Cloudflare. Shuka handles this automatically using a headless browser on Windows and a hidden WebView on Android — no extra setup needed.

## Features

### General
- Downloads and translates Chinese novels to English
- Saves output as a properly formatted `.epub` (cover, title page, chapters)
- Auto-detects cover image from the novel's index page
- Generates a styled SVG cover if no image is found
- Parallel fetch + translate pipeline for faster downloads
- Paste any page URL — Shuka automatically resolves to the correct index
- Extensible adapter system for adding new sites

### Android
- Queue multiple novels at once — each download runs independently
- Downloads continue in the background even when the app is closed or the screen is off
- Auto-retries up to 5 times on error with increasing delay between attempts
- On failure, Retry and Dismiss buttons appear on the download card
- Prevents duplicate downloads — queuing the same URL twice is blocked
- Open or share the finished `.epub` directly from the Downloads tab
- Custom save location with full storage permission handling (Android 11+)
- Four built-in themes: Obsidian, Rosewood, Slate, Frost

## Installation

### Windows
Download and run `Shuka-Windows-vX.X.X.exe` from the [Releases](https://github.com/seizue/Shuka/releases) page. No admin rights required.

The installer places everything in `%LocalAppData%\Shuka`, creates a Start Menu shortcut, and installs the Chromium browser needed for Cloudflare bypass.

### Android
Download `Shuka-Android-vX.X.X.apk` from the [Releases](https://github.com/seizue/Shuka/releases) page and install it. Enable **Install from unknown sources** if prompted.

Default save location is `Downloads/Shuka` on internal storage. You can change this in **Settings → Download Location**.

> On Android 11 and above, Shuka will ask for **All Files Access** when setting a custom save folder.

## Usage

### Windows

Launch **Shuka** from the Start Menu or desktop shortcut:

```
===============================================
       Shuka -> Chinese to English (EPUB)
===============================================

  1. Download single novel
  2. Batch download (multiple novels)
  3. Exit
```

**Single download** — paste the novel URL, optionally provide a cover image URL, and choose how many chapters to download (0 = all). The `.epub` is saved to your Downloads folder.

**Batch download** — add novels one by one, then start. All novels download sequentially, one `.epub` each.

```
--- Novel #1 ---
Novel URL:  https://czbooks.net/n/clgajm
Cover URL:  (optional)
Novel #1 added.

  1. Add another novel
  2. Start downloading (1 queued)
  3. Cancel
```

### Command line

```bash
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

### Android

1. Open the app and paste a novel URL into the **Novel URL** field
2. Optionally set a cover URL and chapter limit (0 = all)
3. Tap **Download & Translate** — the download is queued immediately
4. Switch to the **Downloads** tab to monitor progress, cancel, or manage finished downloads
5. Once done, tap **Open** to read in your e-reader app or **Share** to send the file

## Building from source

Requires [.NET 9 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build -c Release
```

**Windows installer** — publish first then compile with [Inno Setup](https://jrsoftware.org/isinfo.php):

```bash
dotnet publish -c Release -r win-x64 --self-contained false -o bin/publish
Shuka.exe playwright install chromium
ISCC.exe installer.iss
```

**Android APK:**

```bash
dotnet publish Shuka.Android/Shuka.Android.csproj -f net9.0-android -c Release
```

## Adding a new site

Implement `ISiteAdapter` in `Shuka.Core` and register it in `BookService`:

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

## License

See [LICENSE](LICENSE).
