using WPAIPoster.BlogPost;
using WPAIPoster.Config;
using WPAIPoster.Images;
using WPAIPoster.Llm;
using WPAIPoster.Wordpress;

// ---- Argument parsing ----------------------------------------------------

var positionals = new List<string>();
bool setPassword = false;
bool setKeyPassword = false;
bool? publishOverride = null;
bool noImages = false;
bool debug = false;

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

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(600) };
ILlmClient textClient = LlmClientFactory.Create(http, settings.Provider, settings.Model, settings.BaseUrl, settings.ApiKey);
ILlmClient visionClient = LlmClientFactory.Create(
    http, settings.Provider, settings.VisionModel ?? settings.Model, settings.BaseUrl, settings.ApiKey);

var tempImages = new List<string>();

try
{
    using ISshRunner runner = SshNetRunner.Connect(sshConfig, protector);
    Console.WriteLine($"Connected to {sshConfig.Username}@{sshConfig.Server}:{sshConfig.EffectivePort}.");

    string folder = settings.WordPressFolder ?? ".";

    // 1. Existing posts (internal-link context).
    var fetcher = new ExistingPostsFetcher(runner, folder);
    var existing = fetcher.Fetch();
    Console.WriteLine($"Found {existing.Count} published post(s) for internal-link context.");
    string existingText = ExistingPostsFetcher.FormatForPrompt(existing);

    // 2. Generate the post.
    Console.WriteLine("Generating blog post...");
    BlogPostResult post = await BlogPostGenerator.Create(textClient).GenerateAsync(brief, existingText);
    PrintPost(post);

    // 3. Select + prepare images.
    var prepared = new List<SelectedImage>();
    if (!noImages && imagesPerPost > 0)
    {
        Console.WriteLine();

        // 3a. Index the library's tags and pre-select images by tag relevance (cheap, no vision calls).
        ImageTagCatalog catalog = ImageLibraryScanner.ScanWithTags(
            settings.ImageLibrary, maxImagesToIndex, new ImageTagReader(), tagPrefix);
        Console.WriteLine($"Indexed {catalog.Images.Count} image(s) ({catalog.TaggedCount} tagged).");

        IReadOnlyList<string> tagPicked = await TagBasedImageSelector.Create(textClient)
            .SelectAsync(catalog, post, tagCandidateLimit);
        Console.WriteLine($"Tag matching selected {tagPicked.Count} image(s).");

        // 3b. Top up with newest images, then vision-score the candidate set against the themes.
        IReadOnlyList<string> candidates = CandidateSet.Build(tagPicked, catalog.NewestPaths, maxImagesToScore);
        Console.WriteLine($"Vision-scoring {candidates.Count} candidate(s) against themes: " +
                          $"{string.Join(", ", post.ImageThemes)}");

        var selected = await ImageRelevanceSelector.Create(visionClient).SelectAsync(
            candidates, post.ImageThemes, imagesPerPost,
            onScored: (i, total, name, score) => Console.WriteLine(
                double.IsNaN(score)
                    ? $"  [{i}/{total}] {name} — skipped (unreadable)"
                    : $"  [{i}/{total}] {name} — relevance {score:0.00}"));

        Console.WriteLine($"Selected {selected.Count} image(s):");
        foreach (SelectedImage img in selected)
        {
            string dest = Path.Combine(Path.GetTempPath(), $"wpaiposter-{Guid.NewGuid():N}.jpg");
            long size = ImagePreparer.PrepareForUpload(img.Path, dest);
            tempImages.Add(dest);
            prepared.Add(img with { Path = dest });
            Console.WriteLine($"  {(img.IsFeatured ? "★ featured " : "  ")}{Path.GetFileName(img.Path)} " +
                              $"(score {img.Score:0.00}, prepared {size / 1024}KB)");
        }
    }

    // 4. Publish.
    Console.WriteLine(publish ? "Publishing post..." : "Creating draft...");
    var publisher = new WpCliPublisher(runner, folder, settings.SeoMetaKeys,
        settings.DefaultCategory ?? AppLimits.DefaultCategory);
    PublishOutcome outcome = publisher.Publish(post, prepared, publish);

    Console.WriteLine($"\nDone. Post ID {outcome.PostId} ({(outcome.Published ? "published" : "draft")}).");
    if (outcome.AdminEditUrl is { Length: > 0 })
        Console.WriteLine($"Edit: {outcome.AdminEditUrl}");
    return 0;
}
catch (Renci.SshNet.Common.SshException ex)
{
    DumpError("SSH error", ex, debug);
    return 1;
}
catch (HttpRequestException ex)
{
    DumpError("AI provider connection error", ex, debug);
    return 1;
}
catch (Exception ex)
{
    DumpError("Error", ex, debug);
    return 1;
}
finally
{
    foreach (string f in tempImages)
        try { File.Delete(f); } catch { /* ignore */ }
}

// ---- Helpers --------------------------------------------------------------

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

static void PrintPost(BlogPostResult post)
{
    const string rule = "────────────────────────────────────────────────────────";
    Console.WriteLine();
    Console.WriteLine(rule);
    Console.WriteLine($"Meta Title:       {post.MetaTitle}");
    Console.WriteLine($"Meta Description: {post.MetaDescription}");
    Console.WriteLine($"H1:               {post.H1}");
    if (post.ImageThemes.Count > 0)
        Console.WriteLine($"Image Themes:     {string.Join(", ", post.ImageThemes)}");
    if (post.Tags.Count > 0)
        Console.WriteLine($"Tags:             {string.Join(", ", post.Tags.Take(AppLimits.MaxPostTags))}");
    if (post.Categories.Count > 0)
        Console.WriteLine($"Categories:       {string.Join(", ", post.Categories)}");
    if (post.InternalLinks.Count > 0)
    {
        Console.WriteLine("Internal Links:");
        foreach (var link in post.InternalLinks)
            Console.WriteLine($"  - {link.Anchor} → {link.Url}");
    }
    if (!string.IsNullOrWhiteSpace(post.Cta))
        Console.WriteLine($"CTA:              {post.Cta}");
    Console.WriteLine(rule);
    Console.WriteLine("Body:");
    Console.WriteLine(post.BodyHtml);
    Console.WriteLine(rule);
}

static void DumpError(string prefix, Exception ex, bool debug)
{
    Console.Error.WriteLine($"{prefix}: {ex.Message}");
    if (!debug)
    {
        Console.Error.WriteLine("(re-run with --debug for the full stack trace)");
        return;
    }

    // Full exception chain + stack traces.
    for (Exception? e = ex; e is not null; e = e.InnerException)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"--- {e.GetType().FullName}: {e.Message}");
        Console.Error.WriteLine(e.StackTrace);
    }
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
          --debug          Print full stack traces on error.
          -h, --help       Show this help.

        Configuration: app.settings.json (AI + app) and ssh-config.json (SSH).
        """);
}
