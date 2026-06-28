using Spectre.Console;
using WPAIPoster.BlogPost;
using WPAIPoster.Config;
using WPAIPoster.Images;
using WPAIPoster.Llm;
using WPAIPoster.Ui;
using WPAIPoster.Wordpress;

// ---- Argument parsing ----------------------------------------------------

var positionals = new List<string>();
bool setPassword = false;
bool setKeyPassword = false;
bool? publishOverride = null;
bool noImages = false;
bool debug = false;
Verbosity verbosity = Verbosity.Normal;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--set-ssh-password": setPassword = true; break;
        case "--set-key-password": setKeyPassword = true; break;
        case "--publish": publishOverride = true; break;
        case "--draft": publishOverride = false; break;
        case "--no-images": noImages = true; break;
        case "--debug": debug = true; break;
        case "--verbose" or "-v": verbosity = Verbosity.Verbose; break;
        case "--quiet" or "-q": verbosity = Verbosity.Quiet; break;
        case "-h" or "--help": PrintUsage(); return 0;
        default: positionals.Add(args[i]); break;
    }
}

AppSettings settings = AppSettings.Load();
SshConfig sshConfig = SshConfig.Load();
string sshConfigPath = sshConfig.LoadedFrom
    ?? Path.Combine(Directory.GetCurrentDirectory(), "ssh-config.json");
var protector = new SshConfigProtector(SshConfigProtector.KeyFilePathFor(sshConfigPath));

// ---- --set-ssh-password verb ---------------------------------------------

if (setPassword)
{
    Console.Write("Enter SSH login password to encrypt: ");
    string password = ReadSecret();
    sshConfig.PasswordEnc = protector.Protect(password);
    sshConfig.Save(sshConfigPath);
    Console.WriteLine($"\nEncrypted login password (passwordEnc) saved to {sshConfigPath}.");
    return 0;
}

if (setKeyPassword)
{
    Console.Write("Enter private key passphrase to encrypt: ");
    string passphrase = ReadSecret();
    sshConfig.PrivateKeyPwdEnc = protector.Protect(passphrase);
    sshConfig.Save(sshConfigPath);
    Console.WriteLine($"\nEncrypted key passphrase (privateKeyPwdEnc) saved to {sshConfigPath}.");
    return 0;
}

// ---- Resolve the brief ----------------------------------------------------

string brief = positionals.Count > 0
    ? string.Join(' ', positionals)
    : Prompt("Describe the blog post you want to write:");

if (string.IsNullOrWhiteSpace(brief))
{
    Console.Error.WriteLine("No blog post brief provided.");
    return 1;
}

// Pull any author "[TAGS: …]" directive out of the brief: the tags steer image selection (highest
// priority) and the directive is stripped so it never reaches the generator / published post.
(IReadOnlyList<string> userTags, brief) = BriefTags.Parse(brief);

if (string.IsNullOrWhiteSpace(settings.Model))
{
    Console.Error.WriteLine("No 'model' configured in app.settings.json.");
    return 1;
}

bool publish = publishOverride ?? settings.AutoPublish ?? false;
int imagesPerPost = settings.ImagesPerPost ?? AppLimits.DefaultImagesPerPost;
int maxImagesToScore = settings.MaxImagesToScore ?? AppLimits.DefaultMaxImagesToScore;
int maxImagesToIndex = settings.MaxImagesToIndex ?? AppLimits.DefaultMaxImagesToIndex;
string tagPrefix = settings.TagPrefix ?? AppLimits.DefaultTagPrefix;
int tagCandidateLimit = settings.TagCandidateLimit ?? AppLimits.DefaultTagCandidateLimit;
int imageDedupThreshold = settings.ImageDedupThreshold ?? AppLimits.DefaultImageDedupThreshold;
double minImageRelevance = settings.MinImageRelevance ?? AppLimits.DefaultMinImageRelevance;
bool avoidRecentFeatured = settings.AvoidRecentFeaturedImages ?? AppLimits.DefaultAvoidRecentFeaturedImages;
int recentFeaturedHistoryCount = settings.RecentFeaturedHistoryCount ?? AppLimits.DefaultRecentFeaturedHistoryCount;
int recentFeaturedThreshold = settings.RecentFeaturedHammingThreshold ?? AppLimits.DefaultRecentFeaturedHammingThreshold;
string outputFolder = settings.OutputFolder ?? AppLimits.DefaultOutputFolder;

// ---- Logging + UI ---------------------------------------------------------

var logger = new RunLogger(outputFolder, DateTime.Now, Guid.NewGuid().ToString("N"));
var ui = new Ui(AnsiConsole.Console, logger, verbosity);

ui.Rule("WPAIPoster");
ui.Detail($"Brief: {brief}");
ui.Detail($"Provider: {settings.Provider}, model: {settings.Model}, vision: {settings.VisionModel ?? settings.Model}");
ui.Detail($"Publish: {publish}, images: {(noImages ? "off" : imagesPerPost.ToString())}, output: {outputFolder}");

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(600) };
ILlmClient textClient = new LoggingLlmClient(
    LlmClientFactory.Create(http, settings.Provider, settings.Model, settings.BaseUrl, settings.ApiKey),
    logger, "text");
ILlmClient visionClient = new LoggingLlmClient(
    LlmClientFactory.Create(http, settings.Provider, settings.VisionModel ?? settings.Model, settings.BaseUrl, settings.ApiKey),
    logger, "vision");

var tempImages = new List<string>();

try
{
    using ISshRunner runner = ui.Status("Connecting to the WordPress server",
        () => SshNetRunner.Connect(sshConfig, protector));
    ui.Success($"Connected to {sshConfig.Username}@{sshConfig.Server}:{sshConfig.EffectivePort}");

    string folder = settings.WordPressFolder ?? ".";

    // 1. Existing posts (internal-link context).
    var fetcher = new ExistingPostsFetcher(runner, folder);
    var existing = ui.Status("Fetching existing posts for internal-link context", () => fetcher.Fetch());
    ui.Success($"Found {existing.Count} published post(s) for internal-link context");
    string existingText = ExistingPostsFetcher.FormatForPrompt(existing);

    // 2. Generate the post (optionally gated by the Editor reviewer, which drives rewrites).
    ui.Rule("Generate");
    IReadOnlyList<string> briefLinks = BriefLinks.ExtractUrls(brief);
    if (briefLinks.Count > 0)
        ui.Info($"Found {briefLinks.Count} source link(s) in the brief to carry through");
    if (userTags.Count > 0)
        ui.Info($"Using {userTags.Count} author tag(s) for image selection: {string.Join(", ", userTags)}");

    var generator = BlogPostGenerator.Create(textClient);
    BlogPostResult post = await ui.StatusAsync("Generating blog post",
        () => generator.GenerateAsync(brief, existingText, sourceLinks: briefLinks));
    ui.Success("Draft generated");

    if (settings.EnableEditorReviewer ?? false)
    {
        double threshold = settings.EditorReviewerThreshold ?? AppLimits.DefaultEditorReviewerThreshold;
        var reviewer = EditorReviewer.Create(textClient);

        EditorReview review = await ui.StatusAsync("Editor reviewing draft", () => reviewer.ReviewAsync(brief, post));
        var feedbackNotes = new List<string>(); // accumulated across rounds so rewrites retain earlier notes

        for (int rewrite = 0; rewrite < AppLimits.MaxEditorRevisions; rewrite++)
        {
            if (review.IsUnscored)
            {
                ui.Warn("Editor review could not be parsed — accepting the draft");
                break;
            }

            ui.Info($"Editor score: {review.Score:0.00} (threshold {threshold:0.00})");
            if (review.Score >= threshold)
                break;

            if (!feedbackNotes.Any(n => string.Equals(n, review.Feedback, StringComparison.OrdinalIgnoreCase)))
                feedbackNotes.Add(review.Feedback);
            string combinedFeedback = EditorReviewer.CombineFeedback(feedbackNotes);

            ui.Warn($"Editor requested a rewrite (attempt {rewrite + 1}/{AppLimits.MaxEditorRevisions})");
            ui.Detail($"Editor feedback (cumulative, {feedbackNotes.Count} round(s)):\n{combinedFeedback}");
            post = await ui.StatusAsync("Rewriting blog post",
                () => generator.GenerateAsync(brief, existingText, combinedFeedback, briefLinks));
            review = await ui.StatusAsync("Editor reviewing rewrite", () => reviewer.ReviewAsync(brief, post));
        }

        if (!review.IsUnscored)
        {
            if (review.Score >= threshold)
                ui.Success($"Editor approved the draft (score {review.Score:0.00})");
            else
                ui.Warn($"Editor still below threshold (score {review.Score:0.00}) after {AppLimits.MaxEditorRevisions} rewrite(s); proceeding with the best draft");
        }
    }

    // Guarantee every brief URL made it into the body (append a Sources list for any the model dropped).
    string ensuredBody = BriefLinks.EnsureLinksPresent(post.BodyHtml, briefLinks);
    if (!ReferenceEquals(ensuredBody, post.BodyHtml))
    {
        post.BodyHtml = ensuredBody;
        ui.Info("Added a Sources section for brief link(s) the model did not include");
    }

    // Links to other sites should open in a new tab; internal links (this blog's domain) stay in-tab.
    post.BodyHtml = ExternalLinks.MarkExternalLinksNewTab(post.BodyHtml, settings.WordPressFolder);

    ui.RenderPost(post);

    // 3. Select + prepare images.
    var prepared = new List<SelectedImage>();
    if (!noImages && imagesPerPost > 0)
    {
        ui.Rule("Images");

        // 3a. Index the library's tags and pre-select images by tag relevance (cheap, no vision calls).
        ImageTagCatalog catalog = ui.Status("Indexing image library",
            () => ImageLibraryScanner.ScanWithTags(settings.ImageLibrary, maxImagesToIndex, new ImageTagReader(), tagPrefix));
        ui.Success($"Indexed {catalog.Images.Count} image(s) ({catalog.TaggedCount} tagged)");

        IReadOnlyList<string> tagPicked = await ui.StatusAsync("Tag matching against the post",
            () => TagBasedImageSelector.Create(textClient).SelectAsync(catalog, post, tagCandidateLimit, userTags));
        ui.Success($"Tag matching selected {tagPicked.Count} image(s)");

        // 3b. Top up with the newest images, then vision-score the candidate set against the themes.
        IReadOnlyList<string> candidates = CandidateSet.Build(tagPicked, catalog.NewestPaths, maxImagesToScore);
        ui.Info($"Vision-scoring against themes: {string.Join(", ", post.ImageThemes.Select(t => $"{t.Subject} ({t.Description})"))}");

        // 3c. Avoid reusing a recent post's featured image: fetch the last N featured images (by content
        //     hash, since the local→WordPress filename link is lost on upload) so the featured pick steers
        //     clear of them. Best-effort — failures just mean no exclusion.
        IReadOnlySet<ulong> recentFeatured = new HashSet<ulong>();
        if (avoidRecentFeatured)
        {
            recentFeatured = ui.Status("Fetching recent featured images to avoid repeats",
                () => new FeaturedHistoryFetcher(runner, folder, DownloadImageStream)
                    .FetchRecentFeaturedHashes(recentFeaturedHistoryCount));
            if (recentFeatured.Count > 0)
                ui.Success($"Loaded {recentFeatured.Count} recent featured fingerprint(s) to avoid");
        }

        var selector = ImageRelevanceSelector.Create(visionClient);
        var selected = await ui.ProgressAsync("Vision-scoring", candidates.Count, sink =>
            selector.SelectAsync(
                candidates, post.ImageThemes, post.H1, post.MetaDescription,
                imagesPerPost, imageDedupThreshold, minImageRelevance,
                onScored: (i, total, name, score, theme) => sink(i, total,
                    double.IsNaN(score)
                        ? $"{name} — skipped (unreadable)"
                        : $"{name} — relevance {score:0.00} (best theme: {theme})"),
                recentFeaturedHashes: recentFeatured,
                recentFeaturedThreshold: recentFeaturedThreshold));

        ui.Success($"Selected {selected.Count} image(s)");
        foreach (SelectedImage img in selected)
        {
            string dest = Path.Combine(Path.GetTempPath(), $"wpaiposter-{Guid.NewGuid():N}.jpg");
            long size = ImagePreparer.PrepareForUpload(img.Path, dest);
            tempImages.Add(dest);
            prepared.Add(img with { Path = dest });
            string themeNote = string.IsNullOrEmpty(img.Theme) ? "" : $", theme: {img.Theme}";
            string star = img.IsFeatured ? "★ featured " : "  ";
            ui.Info($"  {star}{Path.GetFileName(img.Path)} (score {img.Score:0.00}{themeNote}, prepared {size / 1024}KB)");
        }
    }

    // 4. Publish.
    ui.Rule("Publish");
    var publisher = new WpCliPublisher(runner, folder, settings.SeoMetaKeys,
        settings.DefaultCategory ?? AppLimits.DefaultCategory);
    PublishOutcome outcome = publisher.Publish(post, prepared, publish, onStep: ui.Info);

    ui.Success($"Done. Post ID {outcome.PostId} ({(outcome.Published ? "published" : "draft")})");
    if (outcome.AdminEditUrl is { Length: > 0 })
        ui.Info($"Edit: {outcome.AdminEditUrl}");
    return 0;
}
catch (Renci.SshNet.Common.SshException ex)
{
    DumpError("SSH error", ex);
    return 1;
}
catch (HttpRequestException ex)
{
    DumpError("AI provider connection error", ex);
    return 1;
}
catch (Exception ex)
{
    DumpError("Error", ex);
    return 1;
}
finally
{
    foreach (string f in tempImages)
        try { File.Delete(f); } catch { /* ignore */ }

    ui.Info($"Full log: {logger.LogPath}");
    logger.Dispose();
}

// ---- Helpers --------------------------------------------------------------

// Downloads an image over HTTP into memory for perceptual hashing (recent featured-image lookup).
// Returns null on any failure so the caller can skip it gracefully.
Stream? DownloadImageStream(string url)
{
    try
    {
        using HttpResponseMessage resp = http.GetAsync(url).GetAwaiter().GetResult();
        if (!resp.IsSuccessStatusCode)
            return null;
        byte[] bytes = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        return new MemoryStream(bytes);
    }
    catch
    {
        return null;
    }
}

void DumpError(string prefix, Exception ex)
{
    ui.Error($"{prefix}: {ex.Message}");

    // Full exception chain + stack traces always go to the log file; console shows them only with --debug.
    for (Exception? e = ex; e is not null; e = e.InnerException)
    {
        logger.Write("ERROR", $"--- {e.GetType().FullName}: {e.Message}");
        logger.Write("ERROR", e.StackTrace ?? "(no stack trace)");
    }

    if (debug)
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"--- {e.GetType().FullName}: {e.Message}");
            Console.Error.WriteLine(e.StackTrace);
        }
    else
        ui.Info("(re-run with --debug for the full stack trace, or see the log file)");
}

static string Prompt(string message)
{
    Console.WriteLine(message);
    Console.WriteLine("(paste as many lines as you like, then press Ctrl-D on an empty line to finish)");
    Console.Write("> ");
    var lines = new List<string>();
    string? line;
    while ((line = Console.ReadLine()) is not null)
        lines.Add(line);
    return string.Join('\n', lines).Trim();
}

static string ReadSecret()
{
    var chars = new List<char>();
    while (true)
    {
        ConsoleKeyInfo key;
        try { key = Console.ReadKey(intercept: true); }
        catch (InvalidOperationException) { return Console.ReadLine() ?? string.Empty; } // no console (piped)

        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace)
        {
            if (chars.Count > 0) chars.RemoveAt(chars.Count - 1);
            continue;
        }
        if (!char.IsControl(key.KeyChar)) chars.Add(key.KeyChar);
    }
    return new string(chars.ToArray());
}

static void PrintUsage()
{
    Console.WriteLine(
        """
        WPAIPoster — generate and publish a WordPress blog post with AI.

        Usage:
          WPAIPoster "<blog post brief>"      Generate and publish (draft by default).
          WPAIPoster --set-key-password       Encrypt the passphrase that unlocks the private key.
          WPAIPoster --set-ssh-password       Encrypt the SSH login password (basic auth).

        Options:
          --publish        Publish immediately (overrides autoPublish).
          --draft          Force draft (overrides autoPublish).
          --no-images      Skip image selection/upload.
          --verbose, -v    Show full detail on the console (incl. raw model I/O).
          --quiet, -q      Only warnings, errors, and the final result.
          --debug          Print full stack traces on error.
          -h, --help       Show this help.

        Every run writes a full log file to the output folder (default ./Output).
        Configuration: app.settings.json (AI + app) and ssh-config.json (SSH).
        """);
}
