# CLAUDE.md

Guidance for AI agents working in this repository.

## What this is

`WPAIPoster` is a cross-platform .NET 10 console app that turns a short user brief into a structured,
SEO-optimised WordPress blog post. It generates the post with a configurable AI provider (local
**LM Studio** by default; Anthropic / OpenAI / Ollama as alternatives), optionally runs an **Editor
reviewer** LLM that scores the draft and drives rewrites, picks relevant images from a local library by
**per-theme vision-scoring** (with diversity assignment + perceptual-hash dedup), then connects to the
WordPress server over **SSH (SSH.NET)** and publishes via **WP-CLI** — as a draft by default.

The config + LLM-provider patterns are borrowed from the sibling repo
`/home/luke/Documents/Repos/ImageTagger/`.

## Build / test / run

This project targets `net10.0`. The dotnet SDK lives at `~/.dotnet/dotnet` (not always on `PATH`):

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet build WPAIPoster.sln          # build everything
dotnet test  WPAIPoster.sln          # run the xUnit suite (205 tests)
dotnet run --project WPAIPoster.csproj -- "your blog brief"   # run the app
dotnet run --project WPAIPoster.csproj -- --help              # usage
```

## Project layout

The `.sln`, `.csproj`, and all source sit at the **git root** (there is no extra project subfolder).
The test project is **nested** at `WPAIPoster.Tests/`.

```
WPAIPoster.sln  WPAIPoster.csproj  Program.cs        # entry point + orchestration (top-level statements)
app.settings.json   ssh-config.json                  # runtime config (copied to output)
Config/      AppSettings, SshConfig, SshConfigProtector (AES-GCM), AppLimits
Ui/          RunLogger (per-run log file), Ui (Spectre facade), Verbosity
Llm/         ILlmClient + provider clients + LoggingLlmClient + ChatModels/AnthropicModels + LlmClientFactory
Prompts/     blog-post-prompt.json, image-relevance-prompt.json, editor-reviewer-prompt.json,
             tag-to-blog-post-body-prompt.json, PromptLoader (copied to output)
BlogPost/    BlogPostGenerator, BlogPostResult (+ ImageTheme/ImageThemeListConverter),
             BlogPostParser, EditorReviewer
Images/      ImageLibraryScanner, ImageRelevanceSelector, ImagePreparer, PerceptualHash,
             TagBasedImageSelector, TagMatcher, CandidateSet, ImageTagReader
Wordpress/   ISshRunner, SshNetRunner, WpCliCommands, WpCliPublisher,
             ExistingPostsFetcher, HtmlImageEmbedder
WPAIPoster.Tests/   xUnit project (Fakes.cs holds FakeLlmClient / FakeSshRunner)
```

## End-to-end flow (`Program.cs`)

1. Load `app.settings.json` + `ssh-config.json`; build `ILlmClient` (text + vision) and connect `SshNetRunner`.
2. `ExistingPostsFetcher` runs `wp post list` → context for internal links.
3. `BlogPostGenerator` fills the prompt template and parses the model's JSON envelope into `BlogPostResult`.
4. *(Optional, when `enableEditorReviewer`)* `EditorReviewer` scores the draft; if below
   `editorReviewerThreshold`, `BlogPostGenerator.GenerateAsync` re-runs with the editor feedback appended
   (capped at `AppLimits.MaxEditorRevisions` rewrites). A flaky/unparseable review never blocks publishing.
5. `ImageLibraryScanner.ScanWithTags` → `TagBasedImageSelector` (cheap tag pre-filter by **subject**) →
   `CandidateSet.Build` → `ImageRelevanceSelector` (one vision call per image scores it against **every**
   theme description, with post title/summary as context) → diversity assignment (`Select`: best distinct
   image per theme, `PerceptualHash` dedup, `minImageRelevance` floor) → `ImagePreparer` recompresses the
   chosen images under the 500 KB cap.
6. `WpCliPublisher` publishes: SFTP body → `wp post create` → SEO meta → `wp media import`
   (featured + inline) → embed image URLs → `wp post update` → clean up remote temp files.

## Conventions & things to know

- **Provider abstraction**: all LLM calls go through `ILlmClient` (`Llm/`). Add providers in
  `LlmClientFactory.Create`; the default fallback is LM Studio. Provider concrete classes are
  `[ExcludeFromCodeCoverage]` and not unit-tested (they do real HTTP).
- **Structured output**: the model is instructed (in `Prompts/blog-post-prompt.json`) to return a strict
  JSON envelope. `BlogPostParser` tolerates ```json fences and surrounding prose — keep it that way. It
  also **repairs** common LLM-JSON breakage via `RepairJson` (a two-pass parse): unescaped `"` inside
  string values, raw newlines/control chars (e.g. the literal newlines a model emits inside a `<pre>`/
  `<code>` block), and bogus backslash escapes like `\'` (dropped, since they aren't valid JSON escapes).
  It also **strips bare non-JSON garbage** the model leaks into a *structural* position — a stray
  `<em>,` array element, an HTML tag where a value belongs — since that junk lives outside any string and
  so can't be string-repaired; left alone, one bad element would sink the whole envelope even though the
  body and every other field are recoverable. Outside a string the only legal bare tokens are the
  literals `true`/`false`/`null` and numbers (preserved); anything else is dropped, with the dangling
  comma collapsed (array element) or replaced by `null` (object value). `RepairJson` is **key/value-aware**
  — it tracks an object-vs-array container stack so a *value* string may legitimately contain a `":`
  sequence (e.g. the Gutenberg block attribute `{"ordered":true}` embedded in body HTML) without the
  inner quote being mistaken for the string terminator. Tests cover each case.
- **Rich body formatting**: the blog prompt encourages the model to lift posts above plain prose with
  **tables, lists, code, and preformatted blocks** *where they genuinely help* (code blocks only for
  technical topics), each wrapped in its **Gutenberg block comment** (`<!-- wp:table -->`, `<!-- wp:list -->`,
  `<!-- wp:code -->`, `<!-- wp:preformatted -->`) so the block editor treats them as native blocks;
  paragraphs/headings stay plain HTML. The publish path is byte-transparent (SFTP'd file → `wp post create`),
  and WordPress `kses` keeps these tags **and** HTML comments, so the block markup survives to the editor.
  HTML attributes use single quotes; the lone double-quote case is the numbered-list `{"ordered":true}`
  attribute (escaped, with `RepairJson` as the safety net).
- **Image themes** are `{ subject, description }` objects (`ImageTheme`). The **subject** is a short
  taggable noun used by tag matching + display; the **description** is a disambiguating phrase used in the
  vision-scoring prompt (e.g. "network" → "interconnected computer network"). `ImageThemeListConverter`
  reads them tolerantly — each element may be an object *or* a legacy plain string (string → both fields).
- **Diversity image selection**: `ImageRelevanceSelector` scores each candidate against every theme in one
  vision call (`ParseScores` → `double[]`), then `Select` (pure) assigns the best **distinct** image per
  theme, fills leftover slots by best score, skips perceptual near-duplicates (`PerceptualHash` dHash +
  Hamming `imageDedupThreshold`), and never selects an image scoring at/below `minImageRelevance` (so it
  returns *fewer* images rather than padding with irrelevant ones). The post title/meta description are
  passed in as scoring context.
- **Editor reviewer**: `EditorReviewer` (prompt `editor-reviewer-prompt.json`) returns
  `{ score, feedback }`; `ParseReview` reuses `BlogPostParser`'s extract/repair and returns an *unscored*
  (`NaN`) review on unparseable replies so it fails safe (never blocks publishing). Gated by
  `enableEditorReviewer` / `editorReviewerThreshold`; the rewrite loop lives in `Program.cs`.
- **Multi-line brief input**: the interactive `Prompt` in `Program.cs` reads stdin until **EOF (Ctrl-D)**,
  not a single line, so pasted multi-line briefs (tables, code) are captured whole.
- **Terminal UI + logging** (`Ui/`): all pipeline output goes through the `Ui` facade (Spectre.Console —
  colours, `Status` spinners, a `Progress` bar for vision-scoring, `RenderPost` panel), which tees every
  message to a `RunLogger` — a per-run plain-text log file under `outputFolder` (default `./Output`,
  gitignored). `Verbosity` (`--quiet`/`--verbose`) gates the console; the file always gets full detail.
  Raw model I/O is captured by wrapping each `ILlmClient` in `LoggingLlmClient`. Spectre auto-disables
  styling when piped/`NO_COLOR`. `Ui` is `[ExcludeFromCodeCoverage]` (real console I/O); `RunLogger`
  (writes verbatim — never strip brackets, so JSON in the log stays intact) and `LoggingLlmClient` are
  unit-tested. Long-running collaborators
  report progress via callbacks: `WpCliPublisher.Publish(..., onStep)` and
  `ImageRelevanceSelector.SelectAsync(..., onScored)`.
- **WP-CLI commands**: build them only via `WpCliCommands` (pure, shell-quoted, unit-tested). Post
  content is transferred as a remote file (SFTP), never inlined into the command — this avoids shell
  escaping of HTML.
- **SSH auth** (`SshConfig` / `SshNetRunner`): two independent encrypted fields —
  `privateKeyPwdEnc` (passphrase that unlocks the private key at `keyPath`) and `passwordEnc`
  (username/password basic auth). Key auth is offered first. `keyPath` resolves **relative to the
  directory of the loaded `ssh-config.json`**.
- **SSH host-key verification** (anti-MITM): SSH.NET does not verify host keys by default, so
  `SshNetRunner` registers a `HostKeyReceived` handler. `hostKeyFingerprint` (SHA-256, base64) pins the
  server key; when unset it is **trust-on-first-use** — the first connection's key is learned and written
  back to `ssh-config.json`, and later connections are rejected on mismatch with a clear error. Pure
  helpers `NormalizeFingerprint` / `FingerprintsEqual` are unit-tested; the event wiring is integration-only.
- **Secrets**: `SshConfigProtector` uses AES-256-GCM with a random key in a sibling `ssh-config.key`
  (`0600` on Unix). Never commit `ssh-config.key`, `id_rsa`, `*.pem`, `*.key` — they are gitignored.
  Set secrets via the `--set-key-password` / `--set-ssh-password` verbs, not by hand.
- **Testability**: `ISshRunner` and `ILlmClient` are interfaces with fakes in `Tests/Fakes.cs`.
  `WpCliPublisher`, `ImageRelevanceSelector`, `EditorReviewer`, `BlogPostParser`, and `PerceptualHash`
  expose pure helpers (`Select`, `ParseScore`/`ParseScores`, `BuildPrompt`, `RepairJson`, `ParseReview`,
  `Compute`/`HammingDistance`, command builders) so logic can be tested without a server or model.
- **Nested test project gotcha**: because `WPAIPoster.Tests/` is inside the main project dir, the main
  `.csproj` explicitly `<Compile Remove="WPAIPoster.Tests/**/*.cs" />` (plus Content/None/EmbeddedResource).
  If you add file globs to the main project, preserve those removes or the test sources get double-compiled.
- **Content files**: `app.settings.json`, `ssh-config.json`, and `Prompts/*.json` are copied to the
  output (`CopyToOutputDirectory=PreserveNewest`) so prompts stay **end-user editable without a rebuild**.
  The `Prompts/*.json` items use an explicit `Link="Prompts/%(Filename)%(Extension)"` so IDEs (Rider)
  place them in the `Prompts/` subfolder instead of flattening to the output root. `PromptLoader.Load`
  reads `Prompts/<file>` from `AppContext.BaseDirectory` first, then the CWD. Tests use the explicit
  `Load(fileName, promptsDir)` overload with a temp dir (the test project doesn't copy prompts).

## Configuration files

- `app.settings.json` — `provider`, `model`, `visionModel`, `baseUrl`, `apiKey`, `imageLibrary`,
  `autoPublish`, `wordPressFolder`, `maxImagesToScore`, `imagesPerPost`, `maxImagesToIndex`, `tagPrefix`,
  `tagCandidateLimit`, `imageDedupThreshold`, `minImageRelevance`, `defaultCategory`,
  `enableEditorReviewer`, `editorReviewerThreshold`, `outputFolder`, `seoMetaKeys`. Nullable (each falls
  back to the matching `AppLimits` default); API key also falls back to `ANTHROPIC_API_KEY` / `OPENAI_API_KEY`.
- `ssh-config.json` — `server`, `port`, `username`, `keyPath`, `privateKeyPwdEnc`, `passwordEnc`,
  `sshExecutablePath` (reserved/unused by the SSH.NET path).

## Not yet implemented

- Video upload (the `wp media import` path would extend to it, but v1 is images only).
- Live WordPress round-trip is covered only by fake-`ISshRunner` tests, not an integration test.
