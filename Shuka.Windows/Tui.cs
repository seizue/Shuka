using Spectre.Console;
using Shuka.Core;

namespace Shuka;

/// <summary>
/// Interactive TUI for Shuka — launched when no arguments are passed
/// or when --ui is specified. Wraps the same Downloader pipeline used
/// by the CLI, with a nicer Spectre.Console interface.
/// </summary>
internal static class Tui
{
    public static async Task RunAsync(Downloader downloader, bool defaultTranslate = true)
    {
        while (true)
        {
            AnsiConsole.Clear();
            RenderHeader();

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[grey]What would you like to do?[/]")
                    .HighlightStyle(new Style(Color.IndianRed1))
                    .AddChoices(
                        "Download single novel",
                        "Batch download (multiple novels)",
                        "EPUB from checkpoint",
                        "Fix Cloudflare (--solve-cf)",
                        "View supported sites",
                        "About Shuka",
                        "Exit"));

            switch (choice)
            {
                case "Download single novel":
                    await RunSingleAsync(downloader, defaultTranslate);
                    break;
                case "Batch download (multiple novels)":
                    await RunBatchAsync(downloader, defaultTranslate);
                    break;
                case "EPUB from checkpoint":
                    await RunSampleAsync(downloader, defaultTranslate);
                    break;
                case "Fix Cloudflare (--solve-cf)":
                    await RunSolveCfAsync();
                    AnsiConsole.MarkupLine("\n[grey]Press any key to return to menu...[/]");
                    Console.ReadKey(intercept: true);
                    break;
                case "View supported sites":
                    RunViewSites();
                    AnsiConsole.MarkupLine("\n[grey]Press any key to return to menu...[/]");
                    Console.ReadKey(intercept: true);
                    break;
                case "About Shuka":
                    RunAbout();
                    AnsiConsole.MarkupLine("\n[grey]Press any key to return to menu...[/]");
                    Console.ReadKey(intercept: true);
                    break;
                case "Exit":
                    AnsiConsole.MarkupLine("\n[grey]Goodbye![/]");
                    return;
            }
        }
    }

    // ── Single download ───────────────────────────────────────────────────────

    private static async Task RunSingleAsync(Downloader downloader, bool defaultTranslate = true)
    {
        AnsiConsole.Clear();
        RenderHeader();
        AnsiConsole.MarkupLine("[bold yellow]  Single Novel[/]\n");

        var action = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[grey]Ready to download a single novel.[/]")
                .HighlightStyle(new Style(Color.IndianRed1))
                .AddChoices("Continue", "Back to menu"));

        if (action == "Back to menu") return;

        string url = AnsiConsole.Ask<string>("[cyan]Novel URL:[/]").Trim();
        if (string.IsNullOrWhiteSpace(url)) return;

        string coverInput = AnsiConsole.Prompt(
            new TextPrompt<string>("[grey]Cover URL[/] [dim](leave blank to auto-detect)[/]:")
                .AllowEmpty());
        string? cover = string.IsNullOrWhiteSpace(coverInput) ? null : coverInput.Trim();

        string chapterInput = AnsiConsole.Prompt(
            new TextPrompt<string>("[grey]Chapters[/] [dim](0 = all, e.g. 50 or 243-244)[/]:")
                .DefaultValue("0")
                .AllowEmpty());

        int chapterLimit = 0;
        int chapterFrom = 0;
        if (!string.IsNullOrWhiteSpace(chapterInput))
        {
            string chapArg = chapterInput.Trim();
            if (chapArg.Contains('-'))
            {
                var parts = chapArg.Split('-');
                if (parts.Length == 2 &&
                    int.TryParse(parts[0], out int from) &&
                    int.TryParse(parts[1], out int to) &&
                    from >= 1 && to >= from)
                {
                    chapterFrom = from;
                    chapterLimit = to - from + 1;
                }
            }
            else
            {
                int.TryParse(chapArg, out chapterLimit);
            }
        }

        // Skip translation prompt for English-only sources (noveldex.io)
        bool isEnglishSource = url.Contains("noveldex.io", StringComparison.OrdinalIgnoreCase);
        bool translate;
        if (isEnglishSource)
        {
            translate = false;
            AnsiConsole.MarkupLine("[grey]Translation skipped (noveldex.io content is already in English).[/]");
        }
        else
        {
            var translatePrompt = new SelectionPrompt<string>()
                .Title("[grey]Translation behavior?[/]")
                .HighlightStyle(new Style(Color.IndianRed1))
                .AddChoices("Translate to English", "Keep original (no translation)");

            translatePrompt.DefaultValue(defaultTranslate ? "Translate to English" : "Keep original (no translation)");

            var translateChoice = AnsiConsole.Prompt(translatePrompt);
            translate = translateChoice == "Translate to English";
        }

        AnsiConsole.WriteLine();

        await RunDownloadAsync(downloader, url, chapterLimit, cover, translate, chapterFrom);

        var after = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("\n[grey]What would you like to do next?[/]")
                .HighlightStyle(new Style(Color.IndianRed1))
                .AddChoices("Back to menu", "Exit"));

        if (after == "Exit")
        {
            AnsiConsole.MarkupLine("\n[grey]Goodbye![/]");
            Environment.Exit(0);
        }
    }

    // ── Batch download ────────────────────────────────────────────────────────

    private static async Task RunBatchAsync(Downloader downloader, bool defaultTranslate = true)
    {
        AnsiConsole.Clear();
        RenderHeader();
        AnsiConsole.MarkupLine("[bold yellow]  Batch Download[/]\n");
        AnsiConsole.MarkupLine("[grey]Add novels one by one, then start downloading.[/]\n");

        var action = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[grey]Ready to queue novels for batch download.[/]")
                .HighlightStyle(new Style(Color.IndianRed1))
                .AddChoices("Continue", "Back to menu"));

        if (action == "Back to menu") return;

        var queue = new List<(string Url, string? Cover)>();

        while (true)
        {
            AnsiConsole.MarkupLine($"[dim]--- Novel #{queue.Count + 1} ---[/]");

            string url = AnsiConsole.Prompt(
                new TextPrompt<string>("[cyan]Novel URL[/] [dim](blank = back to menu)[/]:")
                    .AllowEmpty()).Trim();

            if (string.IsNullOrWhiteSpace(url))
            {
                if (queue.Count == 0)
                {
                    // Nothing queued — offer to go back or exit
                    var empty = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("[grey]No novels added. What would you like to do?[/]")
                            .HighlightStyle(new Style(Color.IndianRed1))
                            .AddChoices("Back to menu", "Exit"));
                    if (empty == "Exit") { AnsiConsole.MarkupLine("\n[grey]Goodbye![/]"); Environment.Exit(0); }
                    return;
                }
                break;
            }

            string coverInput = AnsiConsole.Prompt(
                new TextPrompt<string>("[grey]Cover URL[/] [dim](leave blank to auto-detect)[/]:")
                    .AllowEmpty());
            string? cover = string.IsNullOrWhiteSpace(coverInput) ? null : coverInput.Trim();

            queue.Add((url, cover));
            AnsiConsole.MarkupLine($"[green]✓ Novel #{queue.Count} added.[/]\n");

            var next = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"[grey]{queue.Count} novel(s) queued. What next?[/]")
                    .HighlightStyle(new Style(Color.IndianRed1))
                    .AddChoices(
                        "Add another novel",
                        $"Start downloading ({queue.Count} queued)",
                        "Back to menu",
                        "Cancel"));

            if (next.StartsWith("Start")) break;
            if (next == "Back to menu") return;
            if (next == "Cancel") return;
        }

        if (queue.Count == 0) return;

        // Check if all URLs are from English-only sources (noveldex.io)
        bool allEnglishSources = queue.All(q => q.Url.Contains("noveldex.io", StringComparison.OrdinalIgnoreCase));
        bool translate;
        if (allEnglishSources)
        {
            translate = false;
            AnsiConsole.MarkupLine("[grey]Translation skipped (all novels are from noveldex.io, content is already in English).[/]");
        }
        else
        {
            var translatePrompt = new SelectionPrompt<string>()
                .Title("[grey]Translation behavior for batch?[/]")
                .HighlightStyle(new Style(Color.IndianRed1))
                .AddChoices("Translate to English", "Keep original (no translation)");

            translatePrompt.DefaultValue(defaultTranslate ? "Translate to English" : "Keep original (no translation)");

            var translateChoice = AnsiConsole.Prompt(translatePrompt);
            translate = translateChoice == "Translate to English";
        }

        AnsiConsole.WriteLine();

        // Show queue summary
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[grey]#[/]").Centered())
            .AddColumn("[grey]URL[/]")
            .AddColumn("[grey]Cover[/]");

        for (int i = 0; i < queue.Count; i++)
            table.AddRow(
                $"[dim]{i + 1}[/]",
                $"[cyan]{Markup.Escape(queue[i].Url)}[/]",
                queue[i].Cover != null ? "[green]custom[/]" : "[dim]auto[/]");

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        for (int i = 0; i < queue.Count; i++)
        {
            AnsiConsole.MarkupLine($"[bold]\n[[{i + 1}/{queue.Count}]] Downloading...[/]");
            await RunDownloadAsync(downloader, queue[i].Url, 0, queue[i].Cover, translate);
        }

        AnsiConsole.MarkupLine("\n[green]✓ Batch complete! Check your Downloads folder.[/]");

        var after = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("\n[grey]What would you like to do next?[/]")
                .HighlightStyle(new Style(Color.IndianRed1))
                .AddChoices("Back to menu", "Exit"));

        if (after == "Exit")
        {
            AnsiConsole.MarkupLine("\n[grey]Goodbye![/]");
            Environment.Exit(0);
        }
        // "Back to menu" — just return, RunAsync loop will redraw the menu
    }

    // ── EPUB from checkpoint ───────────────────────────────────────────────────

    private static async Task RunSampleAsync(Downloader downloader, bool defaultTranslate = true)
    {
        AnsiConsole.Clear();
        RenderHeader();
        AnsiConsole.MarkupLine("[bold yellow]  EPUB from Checkpoint[/]\n");

        string cacheDir = Path.Combine(Path.GetTempPath(), "ShukaCache");
        var checkpoints = CheckpointService.ListAllCheckpoints(cacheDir);

        if (checkpoints.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No saved checkpoints found.[/]\n");
            AnsiConsole.MarkupLine("[dim]Checkpoints are created automatically while downloading. Start a download and pause or interrupt it — it will appear here.[/]");
            AnsiConsole.MarkupLine("\n[grey]Press any key to return to menu...[/]");
            Console.ReadKey(intercept: true);
            return;
        }

        // Build display labels: "Novel URL  (N chapters)"
        // Truncate long URLs for display
        var labels = checkpoints.Select(cp =>
        {
            string display = cp.Url.Length > 70
                ? "..." + cp.Url[^67..]
                : cp.Url;
            return $"{Markup.Escape(display)}  [dim]({cp.Count} ch)[/]";
        }).ToList();
        labels.Add("[grey]Back to menu[/]");

        string picked = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[grey]Select a checkpoint:[/]")
                .HighlightStyle(new Style(Color.IndianRed1))
                .PageSize(12)
                .AddChoices(labels));

        if (picked == "[grey]Back to menu[/]") return;

        // Find the selected checkpoint
        int idx = labels.IndexOf(picked);
        if (idx < 0 || idx >= checkpoints.Count) return;
        var (filePath, url, count) = checkpoints[idx];

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"  [indianred1]│[/] [grey]URL    [/]  [dim]{Markup.Escape(url)}[/]");
        AnsiConsole.MarkupLine($"  [indianred1]│[/] [grey]Saved  [/]  [bold white]{count}[/] chapter(s)");
        AnsiConsole.WriteLine();

        var action = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[grey]What would you like to do?[/]")
                .HighlightStyle(new Style(Color.IndianRed1))
                .AddChoices(
                    "Export EPUB from checkpoint",
                    "Delete checkpoint",
                    "Back to menu"));

        if (action == "Back to menu") return;

        if (action == "Delete checkpoint")
        {
            CheckpointService.Delete(filePath);
            AnsiConsole.MarkupLine("\n[green]✓ Checkpoint deleted.[/]");
            AnsiConsole.MarkupLine("\n[grey]Press any key to return to menu...[/]");
            Console.ReadKey(intercept: true);
            return;
        }

        // Export EPUB
        bool translate = defaultTranslate;
        if (url.Contains("noveldex.io", StringComparison.OrdinalIgnoreCase))
            translate = false;

        AnsiConsole.MarkupLine("\n[grey]Generating EPUB...[/]");
        try
        {
            string? result = await downloader.GenerateSampleEpubAsync(url, null, translate);
            if (result != null)
                AnsiConsole.MarkupLine($"\n[bold green]✓ EPUB created![/] [cyan]{Markup.Escape(result)}[/]");
            else
                AnsiConsole.MarkupLine("\n[red]No chapter data found in checkpoint.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"\n[bold red]Error:[/] {Markup.Escape(ex.Message)}");
        }

        AnsiConsole.MarkupLine("\n[grey]Press any key to return to menu...[/]");
        Console.ReadKey(intercept: true);
    }

    // ── Solve CF ──────────────────────────────────────────────────────────────

    private static async Task RunSolveCfAsync()
    {
        AnsiConsole.Clear();
        RenderHeader();
        AnsiConsole.MarkupLine("[bold yellow]  Fix Cloudflare[/]\n");
        AnsiConsole.MarkupLine("[grey]Opens a visible browser window so you can solve the Cloudflare challenge.[/]");
        AnsiConsole.MarkupLine("[grey]After the page loads, come back here and press Enter.[/]\n");

        var action = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[grey]Ready to open the browser for Cloudflare bypass.[/]")
                .HighlightStyle(new Style(Color.IndianRed1))
                .AddChoices("Continue", "Back to menu"));

        if (action == "Back to menu") return;

        string url = AnsiConsole.Ask<string>("[cyan]Site URL[/] [dim](e.g. https://www.69shuba.com)[/]:").Trim();
        if (string.IsNullOrWhiteSpace(url)) return;

        await PlaywrightFetcher.SolveCfInteractiveAsync(url);
        AnsiConsole.MarkupLine("\n[green]✓ Cloudflare cookies saved. Downloads should now work.[/]");
    }

    // ── Download pipeline with live progress ──────────────────────────────────

    private static async Task RunDownloadAsync(
        Downloader downloader, string url, int chapters, string? cover, bool translate, int chapterFrom = 0)
    {
        BookInfo? book = null;

        // Phase 1: gather book info
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("indianred1"))
            .StartAsync($"[grey]Fetching[/] [dim]{Markup.Escape(url)}[/]", async ctx =>
            {
                try
                {
                    book = await downloader.GatherBookInfoAsync(url, chapters, cover, chapterFrom);
                    ctx.Status("[green]✓ Book info gathered.[/]");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]  ✗ Error: {Markup.Escape(ex.Message)}[/]");
                }
            });

        if (book == null) return;

        AnsiConsole.WriteLine();

        // Show book info using a clean, left-accented vertical block.
        // This avoids any right-border misalignment issues caused by CJK wide-character rendering variances in different terminals.
        string title     = Markup.Escape(book.TitleEn  ?? book.Title);
        string author    = Markup.Escape(book.AuthorEn ?? book.Author);
        string source    = Markup.Escape(book.Adapter.SiteName);
        string chapters2 = book.Total.ToString();

        AnsiConsole.MarkupLine("  [indianred1]Book Info[/]");
        AnsiConsole.MarkupLine($"  [indianred1]│[/] [grey]Title   [/]  [bold white]{title}[/]");
        AnsiConsole.MarkupLine($"  [indianred1]│[/] [grey]Author  [/]  [white]{author}[/]");
        AnsiConsole.MarkupLine($"  [indianred1]│[/] [grey]Source  [/]  [dim]{source}[/]");
        AnsiConsole.MarkupLine($"  [indianred1]│[/] [grey]Chapters[/]  [dim]{chapters2}[/]");
        AnsiConsole.WriteLine();

        if (book.Total == 0)
        {
            AnsiConsole.MarkupLine("[red]No chapters found.[/]");
            return;
        }

        // Phase 2: download loop — supports pause (P) and export (E) via keypress
        await RunDownloadLoopAsync(downloader, book, translate);
    }

    /// <summary>
    /// Inner download loop with pause/resume/export support.
    /// Runs AnsiConsole.Progress in a task, while a background key-reader
    /// watches for P (pause) and E (export EPUB). On pause the loop exits cleanly
    /// (checkpoint data is intact) and the user is prompted to Resume, Export, or Quit.
    /// </summary>
    private static async Task RunDownloadLoopAsync(Downloader downloader, BookInfo book, bool translate)
    {
        // Track how far we got so we can show a meaningful "paused at ch X" message.
        int lastProgress = 0;

        while (true)
        {
            // A fresh CTS for each download run (or resume)
            using var pauseCts  = new CancellationTokenSource();
            bool pauseRequested = false;

            AnsiConsole.MarkupLine("  [dim]P = Pause   E = Export EPUB from checkpoint[/]");
            AnsiConsole.WriteLine();

            // Background key reader — polls while Spectre.Console owns the terminal
            var keyTask = Task.Run(async () =>
            {
                while (!pauseCts.Token.IsCancellationRequested)
                {
                    if (Console.KeyAvailable)
                    {
                        var k = Console.ReadKey(intercept: true);
                        if (k.Key == ConsoleKey.P || k.Key == ConsoleKey.E)
                        {
                            pauseRequested = true;
                            pauseCts.Cancel();
                            break;
                        }
                    }
                    await Task.Delay(80);
                }
            });

            bool downloadComplete = false;
            Exception? downloadError = null;

            try
            {
                await AnsiConsole.Progress()
                    .AutoClear(false)
                    .HideCompleted(false)
                    .Columns(
                        new TaskDescriptionColumn(),
                        new ProgressBarColumn().FinishedStyle(Style.Parse("green")),
                        new PercentageColumn(),
                        new SpinnerColumn(Spinner.Known.Dots) { Style = Style.Parse("indianred1") })
                    .StartAsync(async ctx =>
                    {
                        var task = ctx.AddTask(
                            $"[cyan]{Markup.Escape(book.TitleEn ?? book.Title)}[/]",
                            maxValue: book.Total);

                        await downloader.ProcessBookAsync(book, null,
                            onProgress: (current, total, msg) =>
                            {
                                lastProgress     = current;
                                task.Value       = current;
                                task.Description = $"[cyan]{Markup.Escape(book.TitleEn ?? book.Title)}[/] [dim]{Markup.Escape(msg)}[/]";
                            },
                            translate,
                            pauseCts.Token);

                        task.Value       = book.Total;
                        task.Description = $"[green]✓ {Markup.Escape(book.TitleEn ?? book.Title)}[/]";
                        downloadComplete = true;
                    });
            }
            catch (OperationCanceledException) when (pauseRequested)
            {
                // Paused by the user — not an error
            }
            catch (Exception ex)
            {
                downloadError = ex;
            }

            // Stop the key reader (if not already stopped)
            if (!pauseCts.IsCancellationRequested)
                pauseCts.Cancel();
            await keyTask.ConfigureAwait(false);

            // ── Download finished normally ─────────────────────────────────────
            if (downloadComplete)
            {
                AnsiConsole.MarkupLine($"\n[green]✓ Done! EPUB saved to your Downloads folder.[/]");
                return;
            }

            // ── Error (not a pause) ────────────────────────────────────────────
            if (downloadError != null)
            {
                AnsiConsole.MarkupLine($"\n[bold red]✗ Error:[/] {Markup.Escape(downloadError.Message)}");
                return;
            }

            // ── Paused ────────────────────────────────────────────────────────
            int savedCount = CheckpointService.CountSaved(
                CheckpointService.GetCheckpointPath(
                    Path.Combine(Path.GetTempPath(), "ShukaCache"), book.IndexUrl));

            int pct = book.Total > 0 ? (int)Math.Round(lastProgress * 100.0 / book.Total) : 0;
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"  [yellow]⏸  Paused[/] at chapter [bold]{lastProgress}[/] of [bold]{book.Total}[/] ([dim]{pct}%[/])");
            AnsiConsole.MarkupLine($"  [dim]{savedCount} chapter(s) saved to checkpoint.[/]");
            AnsiConsole.WriteLine();

            // Paused prompt
            var pausedChoice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[grey]What would you like to do?[/]")
                    .HighlightStyle(new Style(Color.IndianRed1))
                    .AddChoices(
                        "Resume download",
                        $"Create EPUB from {savedCount} downloaded chapter(s)",
                        "Quit"));

            if (pausedChoice == "Resume download")
            {
                // Loop back — a fresh CTS will be created at the top of the while(true)
                AnsiConsole.WriteLine();
                continue;
            }

            if (pausedChoice.StartsWith("Create EPUB"))
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"  [grey]Generating EPUB from {savedCount} chapter(s)...[/]");
                try
                {
                    string? epubPath = await downloader.GenerateSampleEpubAsync(
                        book.IndexUrl, null, translate);

                    if (epubPath != null)
                        AnsiConsole.MarkupLine($"\n  [bold green]✓ EPUB created![/] [cyan]{Markup.Escape(epubPath)}[/]");
                    else
                        AnsiConsole.MarkupLine("\n  [red]No checkpoint data found — cannot export.[/]");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"\n  [bold red]✗ Export failed:[/] {Markup.Escape(ex.Message)}");
                }

                AnsiConsole.WriteLine();

                // After exporting, offer to resume or quit
                var afterExport = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[grey]Continue?[/]")
                        .HighlightStyle(new Style(Color.IndianRed1))
                        .AddChoices("Resume download", "Quit"));

                if (afterExport == "Resume download")
                {
                    AnsiConsole.WriteLine();
                    continue;
                }
            }

            // Quit
            AnsiConsole.MarkupLine("\n[grey]Goodbye![/]");
            Environment.Exit(0);
        }
    }

    // ── View supported sites ──────────────────────────────────────────────────

    private static void RunViewSites()
    {
        AnsiConsole.Clear();
        RenderHeader();
        AnsiConsole.MarkupLine("[bold yellow]  Supported Sites[/]\n");

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[grey]Site[/]"))
            .AddColumn(new TableColumn("[grey]Example URL[/]"))
            .AddColumn(new TableColumn("[grey]Notes[/]").Centered());

        table.AddRow(
            "[cyan]52shuku.net[/]",
            "[dim]https://www.52shuku.net/bl/09_b/bkd7d.html[/]",
            "");
        table.AddRow(
            "[cyan]czbooks.net[/]",
            "[dim]https://czbooks.net/n/clgajm[/]",
            "[yellow]CF bypass[/]");
        table.AddRow(
            "[cyan]dmxs.org[/]",
            "[dim]https://www.dmxs.org/GLBH/1840.html[/]",
            "");
        table.AddRow(
            "[cyan]69shuba.com[/]",
            "[dim]https://www.69shuba.com/book/90488.htm[/]",
            "[yellow]CF bypass[/]");
        table.AddRow(
            "[cyan]quanben.io[/]",
            "[dim]https://www.quanben.io/n/aoshidanshen/list.html[/]",
            "");
        table.AddRow(
            "[cyan]situu.cc[/]",
            "[dim]https://www.situu.cc/5_5792/[/]",
            "");
        table.AddRow(
            "[cyan]yamibo.com[/]",
            "[dim]https://www.yamibo.com/novel/267137[/]",
            "");
        table.AddRow(
            "[cyan]zhenhunxiaoshuo.com[/]",
            "[dim]https://www.zhenhunxiaoshuo.com/tadeshantadehai/[/]",
            "");
        table.AddRow(
            "[cyan]noveldex.io[/]",
            "[dim]https://noveldex.io/novel/some-novel[/]",
            "[yellow]WebView (JS)[/]");
        table.AddRow(
            "[cyan]shubaow.net[/]",
            "[dim]https://www.shubaow.net/book/1669.html[/]",
            "");
        AnsiConsole.Write(table);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Sites marked [/][yellow]CF bypass[/][grey] require running [/][indianred1]Fix Cloudflare[/][grey] once before downloading.[/]");
    }

    // ── About ─────────────────────────────────────────────────────────────────

    private static void RunAbout()
    {
        AnsiConsole.Clear();
        RenderHeader();

        var version = System.Reflection.Assembly
            .GetExecutingAssembly()
            .GetName()
            .Version;
        string versionStr = version is not null
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "unknown";

        AnsiConsole.MarkupLine($"[bold yellow]  About Shuka[/]  [dim]v{versionStr}[/]\n");

        AnsiConsole.MarkupLine(
            "  A cross-platform web novel downloader and machine translation (MTL) tool\n" +
            "  that converts Chinese web novels into English [bold].epub[/] for any e-reader.\n");

        AnsiConsole.MarkupLine(
            "  Available as a [indianred1]PowerShell CLI[/] for Windows and an [indianred1]Android app[/]\n" +
            "  built with [bold].NET MAUI[/].\n");

        AnsiConsole.MarkupLine(
            "  [grey]This is an open-source hobby project — built for fun and personal use.[/]\n");

        var infoTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .HideHeaders()
            .AddColumn(new TableColumn("").PadLeft(1))
            .AddColumn(new TableColumn(""));

        infoTable.AddRow(
            "[grey]Open Source[/]",
            "[link=https://github.com/seizue/Shuka][indianred1]github.com/seizue/Shuka[/][/]");
        infoTable.AddRow(
            "[grey]Releases (Windows CLI and Android)[/]",
            "[link=https://github.com/seizue/Shuka/releases][indianred1]github.com/seizure/Shuka/releases[/][/]");

        AnsiConsole.Write(infoTable);
        AnsiConsole.WriteLine();
    }

    private static void RenderHeader()
    {
        // Inner content: "  Shuka  Chinese To English EPUB  " = 34 chars
        // Box width: ║ + 34 + ║ → top/bottom need 34 ═ chars
        AnsiConsole.WriteLine();
        AnsiConsole.Write(
            new Markup("[bold indianred1]  ╔══════════════════════════════════╗[/]\n" +
                       "[bold indianred1]  ║[/]  [bold white]Shuka[/]  [grey]Chinese To English EPUB[/]  [bold indianred1]║[/]\n" +
                       "[bold indianred1]  ╚══════════════════════════════════╝[/]\n"));
        AnsiConsole.WriteLine();
    }
}
