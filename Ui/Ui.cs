using System.Diagnostics.CodeAnalysis;
using Spectre.Console;
using WPAIPoster.BlogPost;
using WPAIPoster.Config;
using WPAIPoster.Llm;

namespace WPAIPoster.Ui;

/// <summary>
/// Single output facade for the app: renders richly to the terminal via Spectre.Console (colours, status
/// spinners, progress bars) and tees every message to the run's <see cref="RunLogger"/>. Console output is
/// gated by <see cref="Verbosity"/>; the log file always receives full detail. This is integration glue
/// over real console I/O, so it is excluded from coverage (cf. the provider clients).
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class Ui(IAnsiConsole console, RunLogger logger, Verbosity verbosity)
{
    /// <summary>Where the full log for this run is being written.</summary>
    public string LogPath => logger.LogPath;

    // ---- Plain messages -------------------------------------------------------

    public void Info(string message)
    {
        logger.Write("INFO", message);
        if (verbosity != Verbosity.Quiet)
            console.MarkupLine(Markup.Escape(message));
    }

    public void Success(string message)
    {
        logger.Write("INFO", message);
        if (verbosity != Verbosity.Quiet)
            console.MarkupLine($"[green]✓[/] {Markup.Escape(message)}");
    }

    public void Warn(string message)
    {
        logger.Write("WARN", message);
        console.MarkupLine($"[yellow]![/] {Markup.Escape(message)}");
    }

    public void Error(string message)
    {
        logger.Write("ERROR", message);
        console.MarkupLine($"[red]✗ {Markup.Escape(message)}[/]");
    }

    /// <summary>Dim detail — only shown on the console in verbose mode, but always logged.</summary>
    public void Detail(string message)
    {
        logger.Write("DEBUG", message);
        if (verbosity == Verbosity.Verbose)
            console.MarkupLine($"[grey]{Markup.Escape(message)}[/]");
    }

    public void Rule(string title)
    {
        logger.Write("INFO", $"── {title} ──");
        if (verbosity != Verbosity.Quiet)
            console.Write(new Rule($"[bold]{Markup.Escape(title)}[/]").LeftJustified());
    }

    public void Blank()
    {
        if (verbosity != Verbosity.Quiet)
            console.WriteLine();
    }

    // ---- Status spinner (indeterminate stages) --------------------------------

    public async Task<T> StatusAsync<T>(string label, Func<Task<T>> work)
    {
        logger.Write("INFO", label);
        if (verbosity == Verbosity.Quiet || !console.Profile.Capabilities.Interactive)
            return await work();

        return await console.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync(label, _ => work());
    }

    public Task StatusAsync(string label, Func<Task> work)
        => StatusAsync(label, async () => { await work(); return true; });

    public T Status<T>(string label, Func<T> work)
    {
        logger.Write("INFO", label);
        if (verbosity == Verbosity.Quiet || !console.Profile.Capabilities.Interactive)
            return work();

        return console.Status().Spinner(Spinner.Known.Dots).Start(label, _ => work());
    }

    // ---- Progress bar (determinate loops) -------------------------------------

    /// <summary>
    /// Runs <paramref name="work"/> with a determinate progress bar. <paramref name="work"/> receives a sink
    /// <c>(completed, total, detail)</c> to call as items finish; each call advances the bar and logs a line.
    /// </summary>
    public async Task<T> ProgressAsync<T>(string label, int total, Func<Action<int, int, string>, Task<T>> work)
    {
        logger.Write("INFO", $"{label}: {total} item(s)");

        void Log(int done, int tot, string detail) =>
            logger.Write("INFO", $"  [{done}/{tot}] {detail}");

        if (verbosity == Verbosity.Quiet || !console.Profile.Capabilities.Interactive)
            return await work(Log);

        return await console.Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new RemainingTimeColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                ProgressTask task = ctx.AddTask(Markup.Escape(label), maxValue: Math.Max(1, total));
                return await work((done, tot, detail) =>
                {
                    task.MaxValue = Math.Max(1, tot);
                    task.Value = done;
                    task.Description = Markup.Escape($"{label} — {detail}");
                    Log(done, tot, detail);
                });
            });
    }

    // ---- LLM outage pause -----------------------------------------------------

    /// <summary>
    /// Pauses after an LLM endpoint exhausted its retries so the user can fix it. Returns <c>true</c> to make
    /// a final attempt (Enter pressed, or the window elapsed) or <c>false</c> to skip it (S / Esc). This may
    /// run inside an active <see cref="StatusAsync{T}"/> spinner or <see cref="ProgressAsync{T}"/> bar, and
    /// Spectre refuses nested live displays — so it only prints lines (rendered above the live display).
    /// </summary>
    public async Task<bool> WaitForRecoveryAsync(LlmOutage outage)
    {
        string what = char.ToUpperInvariant(outage.Label[0]) + outage.Label[1..];
        Warn($"{what} endpoint {outage.Endpoint ?? "(provider default)"} (model {outage.Model}) failed " +
             $"{outage.Attempts} time(s): {outage.LastError}");

        TimeSpan wait = outage.RecoveryWait;
        bool interactive = !Console.IsInputRedirected && console.Profile.Capabilities.Interactive;
        if (!interactive)
        {
            Info($"Waiting {FormatWait(wait)} for the LLM server to recover before a final attempt.");
            await Task.Delay(wait);
            return true;
        }

        // Discard keys typed before the pause so a stray Enter doesn't skip the window.
        while (Console.KeyAvailable)
            Console.ReadKey(intercept: true);

        // Always shown (even with --quiet): the user needs to know the run is waiting on them.
        logger.Write("INFO", "Waiting for the user to fix the LLM endpoint");
        console.MarkupLine(Markup.Escape(
            $"Fix it (e.g. load the model in LM Studio) — press Enter to retry now, S to skip, " +
            $"or waiting {FormatWait(wait)} before a final attempt."));

        var deadline = DateTime.UtcNow + wait;
        int[] reminders = [60, 30, 10];
        int nextReminder = reminders.TakeWhile(r => r >= wait.TotalSeconds).Count();
        while (true)
        {
            TimeSpan remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                Info("Recovery window elapsed — making a final attempt");
                return true;
            }

            if (nextReminder < reminders.Length && remaining.TotalSeconds <= reminders[nextReminder])
            {
                console.MarkupLine(Markup.Escape($"  {reminders[nextReminder]}s left before the final attempt…"));
                nextReminder++;
            }

            while (Console.KeyAvailable)
            {
                ConsoleKey key = Console.ReadKey(intercept: true).Key;
                if (key == ConsoleKey.Enter)
                {
                    Info("Retrying now");
                    return true;
                }
                if (key is ConsoleKey.S or ConsoleKey.Escape)
                {
                    Warn($"Skipped the final {outage.Label} attempt");
                    return false;
                }
            }

            await Task.Delay(200);
        }
    }

    private static string FormatWait(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    // ---- Rendered post --------------------------------------------------------

    public void RenderPost(BlogPostResult post)
    {
        // Full detail to the log (including the body).
        logger.Write("INFO", "Generated post:");
        logger.Write("INFO", $"  Meta Title: {post.MetaTitle}");
        logger.Write("INFO", $"  Meta Description: {post.MetaDescription}");
        logger.Write("INFO", $"  H1: {post.H1}");
        logger.Write("INFO", $"  Image Themes: {string.Join("; ", post.ImageThemes.Select(t => $"{t.Subject} — {t.Description}"))}");
        logger.Write("INFO", $"  Tags: {string.Join(", ", post.Tags)}");
        logger.Write("INFO", $"  Categories: {string.Join(", ", post.Categories)}");
        foreach (var link in post.InternalLinks)
            logger.Write("INFO", $"  Internal link: {link.Anchor} -> {link.Url}");
        logger.Write("INFO", $"  CTA: {post.Cta}");
        logger.Write("INFO", $"  Body:\n{post.BodyHtml}");

        if (verbosity == Verbosity.Quiet)
            return;

        var grid = new Grid().AddColumn(new GridColumn().NoWrap().PadRight(2)).AddColumn();
        void Row(string k, string v) => grid.AddRow($"[grey]{Markup.Escape(k)}[/]", Markup.Escape(v));

        Row("Meta Title", post.MetaTitle);
        Row("Meta Description", post.MetaDescription);
        Row("H1", post.H1);
        if (post.ImageThemes.Count > 0)
            Row("Image Themes", string.Join("; ", post.ImageThemes.Select(t => $"{t.Subject} — {t.Description}")));
        if (post.Tags.Count > 0)
            Row("Tags", string.Join(", ", post.Tags.Take(AppLimits.MaxPostTags)));
        if (post.Categories.Count > 0)
            Row("Categories", string.Join(", ", post.Categories));
        foreach (var link in post.InternalLinks)
            Row("Internal Link", $"{link.Anchor} → {link.Url}");
        if (!string.IsNullOrWhiteSpace(post.Cta))
            Row("CTA", post.Cta);

        console.Write(new Panel(grid).Header("[bold]Generated Post[/]").Expand());

        if (verbosity == Verbosity.Verbose)
        {
            console.Write(new Rule("[grey]Body HTML[/]").LeftJustified());
            console.WriteLine(post.BodyHtml);
        }
    }
}
