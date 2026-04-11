# Shuka

A Windows tool that downloads novels from [52shuku.net](https://www.52shuku.net), translates them from Chinese to English via Google Translate, and packages them as `.epub` files ready for any e-reader.

## Features

- Downloads and translates Chinese web novels to English
- Saves output as a properly formatted `.epub` (cover, title page, chapters)
- Auto-detects cover image from the novel's index page
- Generates a styled SVG cover if no image is found
- Single novel or batch download mode
- Parallel fetch + translate pipeline for faster downloads
- Works with any page URL from a novel — automatically resolves to the index

## Installation

Download and run `Shuka_Setup.exe` from the [Releases](../../releases) page. No admin rights required.

The installer places everything in `%LocalAppData%\Shuka` and creates a Start Menu shortcut (and optionally a desktop shortcut).

## Usage

Launch **Shuka** from the Start Menu or desktop shortcut. You'll see a menu:

```
1. Download single novel
2. Batch download (multiple novels)
3. Exit
```

### Single download

Paste the novel URL, optionally provide a cover image URL, and choose how many pages to download (leave blank for all).

```
Novel URL:  https://www.52shuku.net/gl/14_b/bjY59.html
Cover URL:  (optional)
Pages:      (blank = all)
```

The `.epub` is saved to your **Downloads** folder.

### Batch download

Add novels one by one. For each you can provide an optional cover URL. When done, choose to start — all novels download sequentially, one `.epub` each.

```
--- Novel #1 ---
Novel URL:  https://www.52shuku.net/gl/14_b/bjY59.html
Cover URL:  (optional)
Novel #1 added.

  1. Add another novel
  2. Start downloading (1 queued)
  3. Cancel
```

> You can paste any page URL from a novel (e.g. `bjY59_2.html`) — Shuka will automatically resolve it to the correct index page.

## Command line

You can also run `Shuka.exe` directly:

```
# Single novel (all pages)
Shuka.exe <index-url>

# Single novel (first 3 pages, useful for testing)
Shuka.exe <index-url> 3

# Single novel with a custom cover
Shuka.exe <index-url> 0 "" <cover-url>

# Batch from a text file (one URL per line, # for comments)
Shuka.exe --batch urls.txt
```

Output is saved to `%USERPROFILE%\Downloads` by default.

## Building from source

Requires [.NET 9 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build -c Release
```

To build the installer, publish first then run `installer.iss` with [Inno Setup](https://jrsoftware.org/isinfo.php):

```bash
dotnet publish -c Release -r win-x64 --self-contained false -o bin/publish
```

## License

See [LICENSE](LICENSE).
