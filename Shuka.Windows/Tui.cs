using System.Diagnostics;
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
    private static TranslatorSettings _settings = new();

    public static async Task RunAsync(Downloader downloader, bool defaultTranslate = true,
        TranslatorSettings? translatorSettings = null)
    {
        _settings = translatorSettings ?? TranslatorSettings.Load();
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
                        "Browser (Cloudflare engine)",
                        "View supported sites",
                        "Translation Engine",
                        "View logs",
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
                case "Browser (Cloudflare engine)":
                    await RunBrowserStatusAsync();
                    AnsiConsole.MarkupLine("\n[grey]Press any key to return to menu...[/]");
                    Console.ReadKey(intercept: true);
                    break;
                case "View supported sites":
                    RunViewSites();
                    AnsiConsole.MarkupLine("\n[grey]Press any key to return to menu...[/]");
                    Console.ReadKey(intercept: true);
                    break;
                case "Translation Engine":
                    if (translatorSettings != null)
                        await RunTranslationEngineAsync(translatorSettings);
                    else
                    {
                        AnsiConsole.MarkupLine("[grey]Translation Engine settings unavailable.[/]");
                        AnsiConsole.MarkupLine("\n[grey]Press any key to return to menu...[/]");
                        Console.ReadKey(intercept: true);
                    }
                    break;
                case "View logs":
                    await RunViewLogsAsync();
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

        await RunDownloadAsync(downloader, url, chapterLimit, cover, translate, chapterFrom, _settings);

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
            await RunDownloadAsync(downloader, queue[i].Url, 0, queue[i].Cover, translate, 0, _settings);
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
        labels.Add("[red]Clear all checkpoints[/]");
        labels.Add("[grey]Back to menu[/]");

        string picked = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[grey]Select a checkpoint:[/]")
                .HighlightStyle(new Style(Color.IndianRed1))
                .PageSize(12)
                .AddChoices(labels));

        if (picked == "[grey]Back to menu[/]") return;

        if (picked == "[red]Clear all checkpoints[/]")
        {
            bool confirm = AnsiConsole.Confirm("[yellow]Are you sure you want to delete all checkpoint cache files?[/]", false);
            if (confirm)
            {
                int clearedCount = CheckpointService.ClearAll(cacheDir);
                AnsiConsole.MarkupLine($"\n[green]✓ Cleared {clearedCount} checkpoint cache file(s).[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("\n[grey]Cancelled.[/]");
            }
            AnsiConsole.MarkupLine("\n[grey]Press any key to return to menu...[/]");
            Console.ReadKey(intercept: true);
            return;
        }

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
        Downloader downloader, string url, int chapters, string? cover, bool translate, int chapterFrom = 0,
        TranslatorSettings? translatorSettings = null)
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
                    PrintCloudflareHelp(url);
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

        // Check for existing checkpoint
        string cacheDir = Path.Combine(Path.GetTempPath(), "ShukaCache");
        string checkpointPath = CheckpointService.GetCheckpointPath(cacheDir, book.IndexUrl);
        int savedCount = CheckpointService.CountSaved(checkpointPath);
        if (savedCount > 0)
        {
            var resumeChoice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"[yellow]Found existing checkpoint with {savedCount} saved chapter(s) for this novel.[/]")
                    .HighlightStyle(new Style(Color.IndianRed1))
                    .AddChoices(
                        $"Resume download ({savedCount} ch already saved)",
                        "Clear checkpoint cache & start fresh",
                        "Cancel"));

            if (resumeChoice == "Cancel") return;
            if (resumeChoice.StartsWith("Clear"))
            {
                CheckpointService.Delete(checkpointPath);
                AnsiConsole.MarkupLine("[green]✓ Existing checkpoint cleared. Starting fresh download.[/]\n");
            }
        }

        // Phase 2: download loop — supports pause (P) and export (E) via keypress
        await RunDownloadLoopAsync(downloader, book, translate, translatorSettings);
    }

    /// <summary>
    /// Inner download loop with pause/resume/export support.
    /// Runs AnsiConsole.Progress in a task, while a background key-reader
    /// watches for P (pause) and E (export EPUB). On pause the loop exits cleanly
    /// (checkpoint data is intact) and the user is prompted to Resume, Export, or Quit.
    /// </summary>
    private static async Task RunDownloadLoopAsync(
        Downloader downloader, BookInfo book, bool translate, TranslatorSettings? translatorSettings = null)
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

                // Show translation failure notice and offer prompt to retranslate failed chapters
                while (downloader.LastTranslationFailCount > 0)
                {
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine(
                        $"  [yellow]⚠  {downloader.LastTranslationFailCount} chapter(s) had translation failures.[/]");
                    AnsiConsole.MarkupLine(
                        $"  [dim]Failed chapter(s): [/][white]{string.Join(", ", downloader.LastTranslationFailures)}[/]");
                    if (downloader.LastTranslationLogPath != null)
                    {
                        AnsiConsole.MarkupLine(
                            $"  [dim]Log details: [/][grey]{Markup.Escape(downloader.LastTranslationLogPath)}[/]");
                    }
                    AnsiConsole.WriteLine();

                    var retryChoice = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("[grey]Would you like to retranslate the failed chapter(s)?[/]")
                            .HighlightStyle(new Style(Color.IndianRed1))
                            .AddChoices(
                                $"Retranslate {downloader.LastTranslationFailCount} failed chapter(s)",
                                "Keep current EPUB and finish"));

                    if (retryChoice.StartsWith("Retranslate"))
                    {
                        AnsiConsole.WriteLine();
                        int resolved = 0;
                        int remaining = 0;
                        Exception? retryErr = null;

                        await AnsiConsole.Status()
                            .Spinner(Spinner.Known.Dots)
                            .SpinnerStyle(Style.Parse("indianred1"))
                            .StartAsync("[grey]Retranslating failed chapters...[/]", async ctx =>
                            {
                                try
                                {
                                    var res = await downloader.RetranslateFailedChaptersAsync(
                                        (cur, tot, msg) => ctx.Status($"[grey]{Markup.Escape(msg)}[/]"));
                                    resolved = res.resolvedCount;
                                    remaining = res.remainingFailures;
                                }
                                catch (Exception ex)
                                {
                                    retryErr = ex;
                                }
                            });

                        if (retryErr is LocalAiNotInstalledException || retryErr?.InnerException is LocalAiNotInstalledException)
                        {
                            AnsiConsole.WriteLine();
                            AnsiConsole.MarkupLine("  [bold red]✗ Retranslation paused:[/] Local AI is required for offline fallback but is not installed.");
                            AnsiConsole.WriteLine();
                            var engineChoice = AnsiConsole.Prompt(
                                new SelectionPrompt<string>()
                                    .Title("[grey]Select an option:[/]")
                                    .HighlightStyle(new Style(Color.IndianRed1))
                                    .AddChoices(
                                        "Go to Translation Engine menu to download Local AI configuration",
                                        "Stop and keep current EPUB"));

                            if (engineChoice.StartsWith("Go to Translation Engine"))
                            {
                                await RunTranslationEngineAsync(_settings);
                                downloader.Translator.ReloadEngines();
                                continue;
                            }
                            else
                            {
                                break;
                            }
                        }
                        else if (retryErr != null)
                        {
                            AnsiConsole.MarkupLine($"\n  [red]✗ Retranslation error:[/] {Markup.Escape(retryErr.Message)}");
                            break;
                        }

                        if (resolved > 0)
                            AnsiConsole.MarkupLine($"\n  [green]✓ Successfully retranslated {resolved} chapter(s)! EPUB updated.[/]");

                        if (remaining == 0)
                        {
                            AnsiConsole.MarkupLine("  [green]✓ All chapters are now translated.[/]");
                            break;
                        }
                        else
                        {
                            AnsiConsole.MarkupLine($"  [yellow]⚠  {remaining} chapter(s) still could not be translated.[/]");
                        }
                    }
                    else
                    {
                        // User chose to keep current EPUB
                        string cacheDir = Path.Combine(Path.GetTempPath(), "ShukaCache");
                        string cpPath = CheckpointService.GetCheckpointPath(cacheDir, book.IndexUrl);
                        CheckpointService.Delete(cpPath);
                        break;
                    }
                }

                return;
            }

            // ── Error (not a pause) ────────────────────────────────────────────
            if (downloadError != null)
            {
                bool isLocalAiNotInstalled = downloadError is LocalAiNotInstalledException ||
                                             downloadError.InnerException is LocalAiNotInstalledException;

                if (isLocalAiNotInstalled)
                {
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("  [bold red]✗ Translation Failure:[/] Cannot proceed since Local AI is not installed.");
                    AnsiConsole.MarkupLine("  [grey]Google Translate unofficial API hit rate limits, and fallback engines failed.[/]");
                    AnsiConsole.MarkupLine("  [grey]Local AI (Qwen2.5) is required as the offline fallback, but the model (~468MB) is not downloaded.[/]");
                    AnsiConsole.WriteLine();

                    var actionChoice = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("[grey]Select an option:[/]")
                            .HighlightStyle(new Style(Color.IndianRed1))
                            .AddChoices(
                                "Go to Translation Engine menu to download Local AI configuration",
                                "Stop and exit"));

                    if (actionChoice.StartsWith("Go to Translation Engine"))
                    {
                        var settings = translatorSettings ?? TranslatorSettings.Load();
                        await RunTranslationEngineAsync(settings);
                        downloader.Translator.ReloadEngines();

                        var localCheck = new LocalLlamaTranslator();
                        bool canResume = localCheck.IsModelDownloaded() ||
                                         settings.Engine == TranslatorEngine.PlaywrightTranslate;

                        if (canResume)
                        {
                            AnsiConsole.WriteLine();
                            var resumeChoice = AnsiConsole.Prompt(
                                new SelectionPrompt<string>()
                                    .Title("[grey]Configuration updated. Resume downloading now?[/]")
                                    .HighlightStyle(new Style(Color.IndianRed1))
                                    .AddChoices("Resume download", "Return to main menu"));

                            if (resumeChoice == "Resume download")
                            {
                                AnsiConsole.WriteLine();
                                continue;
                            }
                        }
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("  [dim]Download stopped. Progress has been saved in checkpoint.[/]");
                    }
                    return;
                }

                // Check if a Cloudflare block caused the failure
                bool isCfBlock = downloadError is CloudflareBlockedException ||
                                 downloadError.InnerException is CloudflareBlockedException;

                if (isCfBlock)
                {
                    string siteOrigin = Uri.TryCreate(book.IndexUrl, UriKind.Absolute, out var cfUri)
                        ? $"{cfUri.Scheme}://{cfUri.Host}" : book.IndexUrl;

                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("  [bold yellow]⚠  Cloudflare is blocking chapter downloads.[/]");
                    AnsiConsole.MarkupLine($"  [grey]Site:[/] [cyan]{Markup.Escape(siteOrigin)}[/]");
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("  [grey]You need to solve the CF challenge once so the app can store the bypass cookie.[/]");
                    AnsiConsole.WriteLine();

                    var cfFix = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("[grey]Fix Cloudflare now?[/]")
                            .HighlightStyle(new Style(Color.IndianRed1))
                            .AddChoices(
                                $"Open browser for {siteOrigin} — solve it, then press Enter",
                                "Skip — I'll fix it from the main menu later"));

                    if (cfFix.StartsWith("Open browser"))
                    {
                        try
                        {
                            AnsiConsole.MarkupLine($"\n  [dim]Opening browser... complete the challenge then press Enter here.[/]\n");
                            await PlaywrightFetcher.SolveCfInteractiveAsync(siteOrigin);
                            AnsiConsole.MarkupLine("\n  [green]✓ CF cookies saved![/]");
                            AnsiConsole.MarkupLine("  [grey]Re-run your download — it should now bypass Cloudflare automatically.[/]");
                        }
                        catch (Exception solveEx)
                        {
                            AnsiConsole.MarkupLine($"\n  [red]✗ Browser failed: {Markup.Escape(solveEx.Message)}[/]");
                            AnsiConsole.MarkupLine("  [dim]Try using 'Fix Cloudflare (--solve-cf)' from the main menu instead.[/]");
                        }
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("  [dim]Return to the main menu → Fix Cloudflare (--solve-cf) when ready.[/]");
                    }
                }
                else
                {
                    AnsiConsole.MarkupLine($"\n[bold red]✗ Error:[/] {Markup.Escape(downloadError.Message)}");
                    PrintCloudflareHelp(book.IndexUrl);
                }
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

    // ── Browser status ────────────────────────────────────────────────────────

    private static async Task RunBrowserStatusAsync()
    {
        while (true)
        {
            AnsiConsole.Clear();
            RenderHeader();
            AnsiConsole.MarkupLine("[bold yellow]  Browser (Cloudflare Engine)[/]\n");

            var status = PlaywrightFetcher.GetPlaywrightStatus();

            // Status table
            string browserStatus = status.BrowserFound
                ? "[bold green]✓ Installed[/]"
                : "[bold red]✗ Not found[/]";

            string versionStatus;
            if (!status.BrowserFound)
                versionStatus = "[red]—[/]";
            else if (string.IsNullOrEmpty(status.InstalledVersion))
                versionStatus = "[yellow]Unknown (no marker)[/]";
            else if (status.UpToDate)
                versionStatus = $"[green]{Markup.Escape(status.InstalledVersion)}[/] [dim](up to date)[/]";
            else
                versionStatus = $"[yellow]{Markup.Escape(status.InstalledVersion)}[/] [dim]→ update available: [/][white]{Markup.Escape(status.DllVersion)}[/]";

            AnsiConsole.MarkupLine($"  [indianred1]│[/] [grey]Status   [/]  {browserStatus}");
            AnsiConsole.MarkupLine($"  [indianred1]│[/] [grey]Installed[/]  {versionStatus}");
            AnsiConsole.MarkupLine($"  [indianred1]│[/] [grey]Required [/]  [dim]{Markup.Escape(status.DllVersion)}[/]");
            AnsiConsole.WriteLine();

            if (!status.UpToDate)
            {
                string prompt = status.BrowserFound
                    ? $"[yellow]⚠  An update is available[/] [dim]({Markup.Escape(status.InstalledVersion)} → {Markup.Escape(status.DllVersion)})[/]. Update now for best Cloudflare bypass performance."
                    : "[red]✗  No browser found.[/] Install it so Cloudflare-protected sites work.";
                AnsiConsole.MarkupLine($"  {prompt}\n");
            }

            // Build choices based on current state
            var choices = new List<string>();
            if (!status.UpToDate)
                choices.Add(status.BrowserFound ? "Update browser" : "Install browser");
            choices.Add("Back to menu");

            var action = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[grey]What would you like to do?[/]")
                    .HighlightStyle(new Style(Color.IndianRed1))
                    .AddChoices(choices));

            if (action == "Back to menu") return;

            // Install / update — run it then loop back to re-render the status
            AnsiConsole.WriteLine();
            bool success = false;
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("indianred1"))
                .StartAsync("[grey]Installing Chromium browser...[/]", async ctx =>
                {
                    await Task.Run(() =>
                    {
                        success = PlaywrightFetcher.InstallOrUpdateChromium(msg =>
                        {
                            ctx.Status($"[grey]{Markup.Escape(msg)}[/]");
                        });
                    });
                    ctx.Status(success ? "[green]Done.[/]" : "[red]Failed.[/]");
                });

            AnsiConsole.WriteLine();
            if (success)
            {
                AnsiConsole.MarkupLine("[bold green]✓ Browser installed/updated successfully.[/]");
                AnsiConsole.MarkupLine("[dim]  Refreshing status...[/]");
                await Task.Delay(1000);
                // Loop back — screen will re-render with fresh status
            }
            else
            {
                AnsiConsole.MarkupLine("[bold red]✗ Installation failed.[/]");
                AnsiConsole.MarkupLine("[dim]  Check your internet connection and try again, or run:[/]");
                AnsiConsole.MarkupLine("[dim]  Shuka.exe playwright install chromium[/]");
                AnsiConsole.MarkupLine("\n[grey]Press any key to return to menu...[/]");
                Console.ReadKey(intercept: true);
                return;
            }
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

    // ── Logs ──────────────────────────────────────────────────────────────────

    private static async Task RunViewLogsAsync()
    {
        while (true)
        {
            AnsiConsole.Clear();
            RenderHeader();
            AnsiConsole.MarkupLine("[bold yellow]  Logs[/]\n");

            string logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Shuka");

            if (!Directory.Exists(logDir))
                Directory.CreateDirectory(logDir);

            var logFiles = Directory.GetFiles(logDir, "*.log", SearchOption.TopDirectoryOnly)
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .ToList();

            if (logFiles.Count == 0)
            {
                AnsiConsole.MarkupLine("  [green]✓ No log files found.[/]");
                AnsiConsole.MarkupLine($"  [dim]Log directory: {Markup.Escape(logDir)}[/]\n");

                var emptyChoice = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[grey]Options:[/]")
                        .HighlightStyle(new Style(Color.IndianRed1))
                        .AddChoices(
                            "Open logs folder in Explorer",
                            "Back to menu"));

                if (emptyChoice == "Open logs folder in Explorer")
                {
                    TryOpenPath(logDir);
                    continue;
                }
                return;
            }

            var prompt = new SelectionPrompt<string>()
                .Title("[grey]Select a log file to view:[/]")
                .HighlightStyle(new Style(Color.IndianRed1));

            foreach (var file in logFiles)
            {
                string sizeStr = file.Length switch
                {
                    < 1024 => $"{file.Length} B",
                    < 1024 * 1024 => $"{file.Length / 1024.0:F1} KB",
                    _ => $"{file.Length / (1024.0 * 1024):F1} MB"
                };
                prompt.AddChoice($"{file.Name} ({sizeStr}, {file.LastWriteTime:yyyy-MM-dd HH:mm})");
            }
            prompt.AddChoice("Open logs folder in Explorer");
            prompt.AddChoice("Back to menu");

            var picked = AnsiConsole.Prompt(prompt);
            if (picked == "Back to menu") return;

            if (picked == "Open logs folder in Explorer")
            {
                TryOpenPath(logDir);
                continue;
            }

            var selectedFile = logFiles.FirstOrDefault(f => picked.StartsWith(f.Name));
            if (selectedFile == null) continue;

            await DisplayLogFileAsync(selectedFile);
        }
    }

    private static async Task DisplayLogFileAsync(FileInfo file)
    {
        while (true)
        {
            AnsiConsole.Clear();
            RenderHeader();
            AnsiConsole.MarkupLine($"[bold yellow]  Log:[/] [cyan]{Markup.Escape(file.Name)}[/]");
            AnsiConsole.MarkupLine($"  [dim]Path: {Markup.Escape(file.FullName)}[/]\n");

            file.Refresh();
            if (!file.Exists)
            {
                AnsiConsole.MarkupLine("  [yellow]Log file no longer exists.[/]\n");
                AnsiConsole.MarkupLine("  [grey]Press any key to return...[/]");
                Console.ReadKey(intercept: true);
                return;
            }

            string[] allLines;
            try
            {
                allLines = await File.ReadAllLinesAsync(file.FullName);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"  [red]Error reading file: {Markup.Escape(ex.Message)}[/]\n");
                AnsiConsole.MarkupLine("  [grey]Press any key to return...[/]");
                Console.ReadKey(intercept: true);
                return;
            }

            if (allLines.Length == 0)
            {
                AnsiConsole.MarkupLine("  [grey](Log file is empty)[/]\n");
            }
            else
            {
                // Show last 30 lines if file is long
                int take = Math.Min(30, allLines.Length);
                var displayLines = allLines.TakeLast(take).ToArray();
                string preview = string.Join("\n", displayLines.Select(Markup.Escape));

                var panel = new Panel(preview)
                    .Header(allLines.Length > take
                        ? $"[grey]Showing last {take} of {allLines.Length} lines[/]"
                        : $"[grey]{allLines.Length} line(s)[/]")
                    .BorderColor(Color.Grey)
                    .RoundedBorder();

                AnsiConsole.Write(panel);
                AnsiConsole.WriteLine();
            }

            var action = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[grey]Actions:[/]")
                    .HighlightStyle(new Style(Color.IndianRed1))
                    .AddChoices(
                        "Open file in default editor",
                        "Clear / delete this log",
                        "Back to logs list"));

            if (action == "Back to logs list") return;

            if (action == "Open file in default editor")
            {
                TryOpenPath(file.FullName);
                continue;
            }

            if (action == "Clear / delete this log")
            {
                bool confirm = AnsiConsole.Confirm("  [yellow]Are you sure you want to delete this log file?[/]", false);
                if (confirm)
                {
                    try
                    {
                        File.Delete(file.FullName);
                        AnsiConsole.MarkupLine("\n  [green]✓ Log file deleted.[/]");
                        await Task.Delay(800);
                        return;
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"\n  [red]Failed to delete log: {Markup.Escape(ex.Message)}[/]");
                        await Task.Delay(1500);
                    }
                }
            }
        }
    }

    private static void TryOpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"\n  [yellow]Could not open automatically: {Markup.Escape(ex.Message)}[/]");
            AnsiConsole.MarkupLine($"  [grey]Path: {Markup.Escape(path)}[/]");
            Thread.Sleep(1200);
        }
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

    // ── Translation Engine ────────────────────────────────────────────────────

    private static async Task RunTranslationEngineAsync(TranslatorSettings settings)
    {
        while (true)
        {
            AnsiConsole.Clear();
            RenderHeader();
            AnsiConsole.MarkupLine("[bold yellow]  Translation Engine[/]\n");

            // Status panel
            string activeEngine = settings.Engine switch
            {
                TranslatorEngine.Google              => "[green]Google (scraper)[/]",
                TranslatorEngine.LocalLlama          => "[magenta1]Local AI · Qwen2.5 (offline)[/]",
                TranslatorEngine.PlaywrightTranslate => "[yellow]Browser Translate (Playwright)[/]",
                _                                    => settings.Engine.ToString()
            };
            AnsiConsole.MarkupLine($"  [indianred1]│[/] [grey]Active engine  [/]  {activeEngine}");

            // Local AI model download status
            var localLlama  = new LocalLlamaTranslator();
            string llamaStatus = localLlama.IsModelDownloaded()
                ? "[green]✓ Downloaded[/]"
                : "[yellow]✗ Not downloaded (~468MB)[/]";
            AnsiConsole.MarkupLine(
                $"  [indianred1]│[/] [grey]Local AI model [/]  {llamaStatus}");

            AnsiConsole.WriteLine();

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[grey]Select an action:[/]")
                    .HighlightStyle(new Style(Color.IndianRed1))
                    .AddChoices(
                        "Use Google (scraper — default)",
                        "Use Browser Translate (Playwright — immune to rate limits)",
                        "Use Local AI · Qwen2.5 (on-device translation, no API key)",
                        "Back"));

            if (choice == "Back") return;

            if (choice.StartsWith("Use Google"))
            {
                settings.Engine = TranslatorEngine.Google;
                settings.Save();
                AnsiConsole.MarkupLine("\n[green]✓ Google set as active engine.[/]");
                await Task.Delay(800);
                continue;
            }

            if (choice.StartsWith("Use Browser Translate"))
            {
                settings.Engine = TranslatorEngine.PlaywrightTranslate;
                settings.Save();
                AnsiConsole.MarkupLine("\n[green]✓ Browser Translate set as active engine.[/]");
                AnsiConsole.MarkupLine("[dim]  Uses bundled Chromium — no 429 rate limits.[/]");
                AnsiConsole.MarkupLine("[dim]  First translation per session takes a few extra seconds to spin up.[/]");
                await Task.Delay(1500);
                continue;
            }

            if (choice.StartsWith("Use Local AI"))
            {
                if (!localLlama.IsModelDownloaded())
                {
                    AnsiConsole.MarkupLine("\n[yellow]⚠  Qwen2.5 model is not downloaded yet.[/]");
                    AnsiConsole.MarkupLine("[dim]  Size: ~468MB. Stored in %LocalAppData%\\Shuka\\models\\[/]");
                    AnsiConsole.WriteLine();

                    var dlChoice = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("[grey]Download the Qwen2.5 model now?[/]")
                            .HighlightStyle(new Style(Color.IndianRed1))
                            .AddChoices("Download now (~468MB)", "Cancel"));

                    if (dlChoice == "Cancel") continue;

                    bool dlOk = false;
                    Exception? dlErr = null;

                    await AnsiConsole.Status()
                        .Spinner(Spinner.Known.Dots)
                        .SpinnerStyle(Style.Parse("indianred1"))
                        .StartAsync("[grey]Downloading Qwen2.5 model...[/]", async ctx =>
                        {
                            try
                            {
                                await localLlama.DownloadModelAsync(
                                    msg => ctx.Status($"[grey]{Markup.Escape(msg)}[/]"),
                                    CancellationToken.None);
                                dlOk = true;
                            }
                            catch (Exception ex) { dlErr = ex; }
                        });

                    if (!dlOk)
                    {
                        AnsiConsole.MarkupLine($"\n[red]✗ Download failed: {Markup.Escape(dlErr?.Message ?? "unknown")}[/]");
                        AnsiConsole.MarkupLine("\n[grey]Press any key to continue...[/]");
                        Console.ReadKey(intercept: true);
                        continue;
                    }

                    AnsiConsole.MarkupLine("\n[green]✓ Qwen2.5 model downloaded.[/]");
                }

                settings.Engine = TranslatorEngine.LocalLlama;
                settings.Save();
                AnsiConsole.MarkupLine("[green]✓ Local AI (Qwen2.5) set as active engine.[/]");
                AnsiConsole.MarkupLine("[dim]  Runs 100% offline on CPU. First load takes a moment.[/]");
                await Task.Delay(1200);
                continue;
            }
        }
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

    private static void PrintCloudflareHelp(string url)
    {
        bool isCfSite = url.Contains("69shuba.com", StringComparison.OrdinalIgnoreCase) ||
                        url.Contains("yamibo.com",  StringComparison.OrdinalIgnoreCase);
        if (!isCfSite) return;

        // Extract just the origin (scheme + host) for the instructions
        string siteOrigin = url;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            siteOrigin = $"{uri.Scheme}://{uri.Host}";

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  [bold yellow]Cloudflare Challenge Detected / Bypass Needed[/]");
        AnsiConsole.MarkupLine("  [grey]To fix this:[/] [yellow]1.[/] Return to the main menu and select [indianred1]Fix Cloudflare (--solve-cf)[/]");
        AnsiConsole.MarkupLine($"          [yellow]2.[/] Enter the site address ([cyan]{Markup.Escape(siteOrigin)}[/])");
        AnsiConsole.MarkupLine("          [yellow]3.[/] In the browser window that opens, verify Cloudflare, then press Enter.");
        AnsiConsole.MarkupLine("          [yellow]4.[/] Rerun your download — clearance cookies will be reused automatically.");
    }
}
