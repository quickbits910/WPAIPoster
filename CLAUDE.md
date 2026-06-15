# CLAUDE.md

Guidance for AI agents working in this repository.

## What this is

`WPAIPoster` is a cross-platform .NET 10 console app that turns a short user brief into a structured,
SEO-optimised WordPress blog post. It generates the post with a configurable AI provider (local
**LM Studio** by default; Anthropic / OpenAI / Ollama as alternatives), picks relevant images from a
local library by **vision-scoring** them, then connects to the WordPress server over **SSH (SSH.NET)**
and publishes via **WP-CLI** — as a draft by default.

The config + LLM-provider patterns are borrowed from the sibling repo
`/home/luke/Documents/Repos/ImageTagger/`.

## Build / test / run

This project targets `net10.0`. The dotnet SDK lives at `~/.dotnet/dotnet` (not always on `PATH`):

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet build WPAIPoster.sln          # build everything
dotnet test  WPAIPoster.sln          # run the xUnit suite (67 tests)
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
Llm/         ILlmClient + provider clients + ChatModels/AnthropicModels + LlmClientFactory
Prompts/     blog-post-prompt.json, image-relevance-prompt.json, PromptLoader (copied to output)
BlogPost/    BlogPostGenerator, BlogPostResult, BlogPostParser
Images/      ImageLibraryScanner, ImageRelevanceSelector, ImagePreparer
Wordpress/   ISshRunner, SshNetRunner, WpCliCommands, WpCliPublisher,
             ExistingPostsFetcher, HtmlImageEmbedder
WPAIPoster.Tests/   xUnit project (Fakes.cs holds FakeLlmClient / FakeSshRunner)
```

## End-to-end flow (`Program.cs`)

1. Load `app.settings.json` + `ssh-config.json`; build `ILlmClient` (text + vision) and connect `SshNetRunner`.
2. `ExistingPostsFetcher` runs `wp post list` → context for internal links.
3. `BlogPostGenerator` fills the prompt template and parses the model's JSON envelope into `BlogPostResult`.
4. `ImageLibraryScanner` → `ImageRelevanceSelector` (vision scores each candidate) → `ImagePreparer`
   recompresses the chosen images under the 500 KB cap.
5. `WpCliPublisher` publishes: SFTP body → `wp post create` → SEO meta → `wp media import`
   (featured + inline) → embed image URLs → `wp post update` → clean up remote temp files.

## Conventions & things to know

- **Provider abstraction**: all LLM calls go through `ILlmClient` (`Llm/`). Add providers in
  `LlmClientFactory.Create`; the default fallback is LM Studio. Provider concrete classes are
  `[ExcludeFromCodeCoverage]` and not unit-tested (they do real HTTP).
- **Structured output**: the model is instructed (in `Prompts/blog-post-prompt.json`) to return a strict
  JSON envelope. `BlogPostParser` tolerates ```json fences and surrounding prose — keep it that way.
- **WP-CLI commands**: build them only via `WpCliCommands` (pure, shell-quoted, unit-tested). Post
  content is transferred as a remote file (SFTP), never inlined into the command — this avoids shell
  escaping of HTML.
- **SSH auth** (`SshConfig` / `SshNetRunner`): two independent encrypted fields —
  `privateKeyPwdEnc` (passphrase that unlocks the private key at `keyPath`) and `passwordEnc`
  (username/password basic auth). Key auth is offered first. `keyPath` resolves **relative to the
  directory of the loaded `ssh-config.json`**.
- **Secrets**: `SshConfigProtector` uses AES-256-GCM with a random key in a sibling `ssh-config.key`
  (`0600` on Unix). Never commit `ssh-config.key`, `id_rsa`, `*.pem`, `*.key` — they are gitignored.
  Set secrets via the `--set-key-password` / `--set-ssh-password` verbs, not by hand.
- **Testability**: `ISshRunner` and `ILlmClient` are interfaces with fakes in `Tests/Fakes.cs`.
  `WpCliPublisher` and `ImageRelevanceSelector` expose pure helpers (`Select`, `ParseScore`,
  `BuildPrompt`, command builders) so logic can be tested without a server or model.
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
  `autoPublish`, `wordPressFolder`, `maxImagesToScore`, `imagesPerPost`, `seoMetaKeys`. Nullable; API key
  also falls back to `ANTHROPIC_API_KEY` / `OPENAI_API_KEY`.
- `ssh-config.json` — `server`, `port`, `username`, `keyPath`, `privateKeyPwdEnc`, `passwordEnc`,
  `sshExecutablePath` (reserved/unused by the SSH.NET path).

## Not yet implemented

- Video upload (the `wp media import` path would extend to it, but v1 is images only).
- Live WordPress round-trip is covered only by fake-`ISshRunner` tests, not an integration test.
