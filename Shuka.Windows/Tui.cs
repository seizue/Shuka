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

        int chapters = AnsiConsole.Prompt(
            new TextPrompt<int>("[grey]Chapters[/] [dim](0 = all)[/]:")
                .DefaultValue(0)
                .ValidationErrorMessage("[red]Enter a number[/]"));

        var translatePrompt = new SelectionPrompt<string>()
            .Title("[grey]Translation behavior?[/]")
            .HighlightStyle(new Style(Color.IndianRed1))
            .AddChoices("Translate to English", "Keep original (no translation)");
        
        translatePrompt.DefaultValue(defaultTranslate ? "Translate to English" : "Keep original (no translation)");
        
        var translateChoice = AnsiConsole.Prompt(translatePrompt);
        bool translate = translateChoice == "Translate to English";

        AnsiConsole.WriteLine();

        await RunDownloadAsync(downloader, url, chapters, cover, translate);

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

        var translatePrompt = new SelectionPrompt<string>()
            .Title("[grey]Translation behavior for batch?[/]")
            .HighlightStyle(new Style(Color.IndianRed1))
            .AddChoices("Translate to English", "Keep original (no translation)");
        
        translatePrompt.DefaultValue(defaultTranslate ? "Translate to English" : "Keep original (no translation)");
        
        var translateChoice = AnsiConsole.Prompt(translatePrompt);
        bool translate = translateChoice == "Translate to English";

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
        Downloader downloader, string url, int chapters, string? cover, bool translate)
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
                    book = await downloader.GatherBookInfoAsync(url, chapters, cover);
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

        // Phase 2: download + translate with progress bar
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

                try
                {
                    await downloader.ProcessBookAsync(book, null,
                        onProgress: (current, total, msg) =>
                        {
                            task.Value       = current;
                            task.Description = $"[cyan]{Markup.Escape(book.TitleEn ?? book.Title)}[/] [dim]{Markup.Escape(msg)}[/]";
                        }, translate);

                    task.Value       = book.Total;
                    task.Description = $"[green]✓ {Markup.Escape(book.TitleEn ?? book.Title)}[/]";
                }
                catch (Exception ex)
                {
                    task.Description = $"[red]✗ {Markup.Escape(ex.Message)}[/]";
                }
            });
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
