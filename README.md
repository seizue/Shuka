<div align="center">
<img src="https://github.com/user-attachments/assets/92bec4c5-db17-4f5a-bbfa-c8bc658acb1f"
     width="140"
     height="140"
     alt="appicon" />

# Shuka
A cross-platform web novel downloader and machine translation (MTL) tool that converts Chinese web novels into English `.epub` for any e-reader. Available as a **PowerShell CLI for Windows** and an **Android app built with .NET MAUI.**

<p align="center">   
     
[![GitHub Downloads](https://img.shields.io/github/downloads/seizue/Shuka/total)](https://github.com/seizue/Shuka/releases)
[![GitHub Release](https://img.shields.io/github/v/tag/seizue/Shuka)](https://github.com/seizue/Shuka/releases)
[![GitHub License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)


</div>


## Screenshot
<details>
 <summary>🔽 <strong>[ OPEN SCREENSHOT ]</strong></summary>
  <br>
  <img width="1366" alt="Shuka Screenshot"
       src="https://github.com/user-attachments/assets/c07f2852-306a-4c6d-aec6-7336507d673e" />
</details>


### Supported Sites

| Site | Example URL |
|------|------------|
| [69shuba.com](https://www.69shuba.com/) | `https://www.69shuba.com/book/90417.htm` |
| [52shuku.net](https://www.52shuku.net) | `https://www.52shuku.net/bl/09_b/bkd7d.html` |
| [czbooks.net](https://czbooks.net) | `https://czbooks.net/n/clgajm` |
| [dmxs.org](https://www.dmxs.org) | `https://www.dmxs.org/gdjk/22982.html` |
| [quanben.io](https://www.quanben.io) | `https://www.quanben.io/n/aoshidanshen/list.html` |
| [situu.cc](https://www.situu.cc/) | `https://www.situu.cc/5_5792/` |
| [yamibo.com](https://www.yamibo.com/novel/list) | `https://www.yamibo.com/novel/267137` |
| [Zhenhun Xiaoshuo](https://www.zhenhunxiaoshuo.com/linshilanggu/) | `https://www.zhenhunxiaoshuo.com/linshilanggu/` |


> **czbooks.net** and **69shuba.com** is protected by Cloudflare. Shuka handles this automatically using a headless browser on Windows and a hidden WebView on Android — no extra setup needed.


## Building from source

Requires [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (Windows CLI) and [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (Android / MAUI).

```bash
dotnet build -c Release
```

**Windows installer** — publish first then compile with [Inno Setup](https://jrsoftware.org/isinfo.php):

```bash
dotnet publish Shuka.Windows/Shuka.Windows.csproj -c Release -r win-x64 --self-contained true -o Shuka.Windows/bin/publish
Shuka.Windows/bin/publish/Shuka.exe playwright install chromium
ISCC.exe Shuka.Windows/installer.iss
```

**Android APK:**

```bash
dotnet publish Shuka.Android/Shuka.Android.csproj -f net10.0-android -c Release
```

## Adding a new site

Implement `ISiteAdapter` in `Shuka.Core/Adapters/` and register it in [`BookService.Adapters`](Shuka.Core/BookService.cs):

```csharp
// Shuka.Core/Adapters/MySiteAdapter.cs
public class MySiteAdapter : ISiteAdapter
{
    public string SiteName => "mysite.com";

    // Return true if the URL belongs to this site
    public bool Matches(string url) => url.Contains("mysite.com");

    // Normalize an arbitrary page URL to the novel's index/chapter-list URL
    public string NormalizeUrl(string url) => /* strip chapter suffix, etc. */;

    // Parse the index page HTML — return title, author, cover URL, and ordered chapter list
    public IndexInfo ParseIndex(string html, string indexUrl)
    {
        // IndexInfo(title, author, List<ChapterRef>, coverUrl)
        // ChapterRef(url, displayTitle)
        var chapters = new List<ChapterRef> { new("https://mysite.com/ch1", "Chapter 1") };
        return new IndexInfo("Title", "Author", chapters, coverUrl: null);
    }

    // Extract paragraph text from a chapter page
    public List<string> ExtractChapterText(string html) => /* return one string per paragraph */;

    // Set to true only if the site is behind Cloudflare (uses headless browser / WebView)
    public bool RequiresCfBypass => false;
}
```

Then register it in the [`Adapters`](Shuka.Core/BookService.cs#L17) array:

```csharp
// Shuka.Core/BookService.cs
public static readonly ISiteAdapter[] Adapters =
    [new ShukuAdapter(), new CzBooksAdapter(), new DmxsAdapter(), new ShubaAdapter(),
     new QuanbenAdapter(), new SituuAdapter(), new YamiboAdapter(),
     new MySiteAdapter()]; // ← add here
```

That's all — `BookService` will automatically detect and route downloads to your adapter.

## License

See [LICENSE](LICENSE).

