# WPAIPoster

Generate a complete, SEO-optimised **WordPress blog post** from a one-line brief — written by AI,
illustrated with images from your own library, and published to your site over SSH using WP-CLI.

By default it uses **LM Studio** running locally (your content never leaves your machine), but you can
point it at **Anthropic**, **OpenAI**, or **Ollama** instead. Posts are created as **drafts** unless you
ask to publish.

## What it does

1. You give it a short brief (a sentence or two about the post you want). You can paste a multi-line
   brief at the prompt and finish with **Ctrl-D**.
2. It reads your site's existing published posts so it can suggest **real internal links**.
3. The AI writes the post — meta title, meta description, H1, scannable body with H2/H3 sections, a
   call to action, plus **tags** (up to 5), **categories**, and **image themes** — and returns it as
   structured data. To keep posts engaging, it adds **tables, lists, and preformatted/code blocks**
   where they genuinely help (as native **Gutenberg blocks**), rather than walls of plain paragraphs.
4. *(Optional)* An **Editor reviewer** LLM critically scores the draft for publish-readiness. If it
   scores below your quality threshold, the post is **rewritten** with the editor's feedback (clarity of
   audience, why-it-matters, the main takeaway, flow and style) — up to two rounds. Off by default.
5. It indexes the keyword tags on your local **image library** (XMP / xattr / IPTC, as written by
   [ImageTagger](https://github.com/quickbits910)) and shortlists images whose tags match the post. Each
   image theme carries a short **subject** (for tag matching) and a richer **description** (for the vision
   model). It then **vision-scores** each shortlisted image against *every* theme in one call — using the
   post's title/summary as context — and assigns the best **distinct** image to each theme, skipping
   near-identical duplicates (perceptual hash) and anything below a relevance floor, then resizes the
   winners under 500 KB.
6. It connects to your WordPress server over SSH (with host-key verification), uploads everything, and
   creates the post via WP-CLI — draft by default — placing the best image under the H1, the next two under
   the 2nd and 3rd H2s, and a fourth at the bottom, and applying the tags and categories.

## Requirements

- **.NET 10 SDK** to build/run.
- An AI provider:
  - **LM Studio** running locally with a chat + vision-capable model (default), **or**
  - an **Anthropic** / **OpenAI** API key, or a local **Ollama** server.
- **SSH access** to your WordPress server (private key recommended).
- **WP-CLI** installed and working on the server (the `wp` command available in the WordPress folder).
- A local folder of candidate images (JPEG/PNG/WebP).

## Setup

### 1. Configure the AI + app — `app.settings.json`

```json
{
  "provider": "lmstudio",
  "model": "google/gemma-4-26b-a4b",
  "visionModel": "google/gemma-4-26b-a4b",
  "baseUrl": "http://127.0.0.1:1234",
  "apiKey": null,
  "imageLibrary": "/path/to/your/images/",
  "autoPublish": false,
  "wordPressFolder": "yoursite.com",
  "maxImagesToScore": 8,
  "imagesPerPost": 4,
  "maxImagesToIndex": 1000,
  "tagPrefix": "AI.",
  "tagCandidateLimit": 40,
  "imageDedupThreshold": 6,
  "minImageRelevance": 0.0,
  "defaultCategory": "Blog",
  "enableEditorReviewer": false,
  "editorReviewerThreshold": 0.8,
  "outputFolder": "./Output",
  "seoMetaKeys": {
    "title": "_yoast_wpseo_title",
    "description": "_yoast_wpseo_metadesc"
  }
}
```

| Setting | Meaning |
|---|---|
| `provider` | `lmstudio` (default), `ollama`, `openai`, `openai-compatible`, or `anthropic`. |
| `model` | Model used to write the post (and the tag-based image pre-selection). |
| `visionModel` | Vision-capable model used to score images (falls back to `model`). |
| `baseUrl` | Endpoint for local/compatible providers. |
| `apiKey` | Cloud key. Leave `null` and use `ANTHROPIC_API_KEY` / `OPENAI_API_KEY` env vars instead. |
| `imageLibrary` | Local folder scanned for images. |
| `autoPublish` | `false` → draft (default). `true` → publish immediately. |
| `wordPressFolder` | Folder on the server containing the WordPress install (where `wp` runs). |
| `maxImagesToScore` | Cap on how many candidate images are sent to the vision model. |
| `imagesPerPost` | How many images to place in the post (best under the H1, then under the 2nd/3rd H2, then bottom). |
| `maxImagesToIndex` | Max library images whose tags are read/indexed for tag matching. |
| `tagPrefix` | Keyword prefix ImageTagger writes (e.g. `AI.`); stripped before tag matching. |
| `tagCandidateLimit` | Cap on the tag-matched shortlist sent to the model for image pre-selection. |
| `imageDedupThreshold` | Max perceptual-hash Hamming distance at which two selected images count as near-duplicates (default `6`; higher = more aggressive dedup). |
| `minImageRelevance` | Minimum vision score (`0.00`–`1.00`) an image must exceed to be attached; images at/below are never used to pad a theme (default `0.0`, drops only zero-scoring images). |
| `defaultCategory` | Category applied when the model returns none (default `Blog`). |
| `enableEditorReviewer` | `true` → an Editor LLM scores the draft and drives rewrites; `false` (default) skips review. |
| `editorReviewerThreshold` | Minimum Editor score (`0.00`–`1.00`) a draft must reach to be accepted without a rewrite (default `0.80`). |
| `outputFolder` | Folder where each run writes its log file (default `./Output`). |
| `seoMetaKeys` | Post-meta keys for your SEO plugin (defaults to Yoast). Set to `null` to skip. |

### 2. Configure SSH — `ssh-config.json`

`ssh-config.json` is **gitignored** (it holds your real connection details), so it isn't in the repo.
Copy the tracked template and fill it in:

```bash
cp ssh-config-example.json ssh-config.json
```

```json
{
  "server": "your.server.com",
  "port": 22,
  "username": "you",
  "keyPath": "../id_rsa",
  "privateKeyPwdEnc": null,
  "passwordEnc": null,
  "hostKeyFingerprint": null
}
```

- `keyPath` is your **private key**, resolved relative to the folder containing `ssh-config.json`.
- Authentication supports **either or both**:
  - **Private key** (recommended). If the key has a passphrase, encrypt it:
    ```bash
    dotnet run --project WPAIPoster.csproj -- --set-key-password
    ```
    This stores it as `privateKeyPwdEnc`.
  - **Username / password** (basic auth):
    ```bash
    dotnet run --project WPAIPoster.csproj -- --set-ssh-password
    ```
    This stores it as `passwordEnc`.
- `hostKeyFingerprint` pins the server's **SHA-256 host-key fingerprint** to prevent
  man-in-the-middle attacks. Leave it `null` and the first connection **trusts-on-first-use** —
  the key it sees is recorded back into `ssh-config.json`, and every later connection is rejected
  with a clear error if the server's key changes. To pin up front instead, paste the value from
  `ssh-keygen -lf <host_key>` / `ssh -v` (the part after `SHA256:`).

Secrets are encrypted with **AES-256-GCM** using a key kept in a local `ssh-config.key` file
(permissions `0600` on macOS/Linux). That key file — along with `ssh-config.json`, `id_rsa`, and other
key material — is gitignored and must never be committed. Optional overrides: `pinAlgorithms: true`
forces a modern-only handshake (curve25519 / aes256-gcm / ed25519); see `ssh-config-example.json`.

## Usage

```bash
# Build once
dotnet build WPAIPoster.sln

# Create a draft from a brief
dotnet run --project WPAIPoster.csproj -- "A beginner's guide to speeding up a slow WordPress site"

# Publish immediately instead of drafting
dotnet run --project WPAIPoster.csproj -- --publish "Why image compression matters for SEO"

# Force a draft even if autoPublish is true
dotnet run --project WPAIPoster.csproj -- --draft "Draft this one"

# Skip images entirely
dotnet run --project WPAIPoster.csproj -- --no-images "Text-only announcement post"
```

If you don't pass a brief on the command line, the app prompts you for one. You can paste multiple
lines (the whole brief, tables, code, etc.) and finish input with **Ctrl-D** on an empty line.

### Options

| Flag | Effect |
|---|---|
| `--publish` | Publish immediately (overrides `autoPublish`). |
| `--draft` | Force draft (overrides `autoPublish`). |
| `--no-images` | Skip image selection and upload. |
| `--verbose`, `-v` | Show full detail on the console (incl. raw model prompts/replies). |
| `--quiet`, `-q` | Only warnings, errors, and the final result. |
| `--debug` | Print full stack traces on error. |
| `--set-key-password` | Encrypt and store the private-key passphrase. |
| `--set-ssh-password` | Encrypt and store the SSH login password. |
| `-h`, `--help` | Show help. |

When it finishes, it prints the new post ID and an admin edit URL.

### Console output & logs

The run shows colourised, staged progress — status spinners for each step (connect, generate, editor
review, publish) and a progress bar while images are vision-scored. Styling is automatically disabled
when output is piped/redirected or `NO_COLOR` is set.

**Every run writes a full log file** to the output folder (default `./Output/run-<timestamp>-<id>.log`),
including the brief, configuration, each stage, the rendered post, and the **raw model prompts/replies** —
handy for debugging. The console stays concise by default; `--verbose` mirrors the full detail to the
terminal and `--quiet` trims it. The log path is printed at the end of every run.

## How publishing works (under the hood)

The app runs standard WP-CLI commands in your `wordPressFolder`:

- `wp post list` — to gather existing posts for internal-link suggestions.
- `wp post create <file> --post_status=draft|publish --post_excerpt=… --porcelain` — the post body is
  uploaded as a file (via SFTP) and passed to WP-CLI, so HTML is never mangled by shell escaping.
- `wp post meta update …` — writes the SEO meta title/description (if `seoMetaKeys` is set).
- `wp term create <taxonomy> <name> --porcelain` then `wp post term set <id> post_tag|category <ids> --by=id`
  — resolves each tag/category to a term ID (creating it if missing) and assigns them. Tags are capped at 5;
  categories default to `defaultCategory` when the model returns none.
- `wp media import <file> --post_id=… [--featured_image] --porcelain` — uploads each image and attaches it.
- `wp post update <id> <file>` — re-saves the body with the uploaded images embedded at structural
  positions (best under the H1, next two under the 2nd/3rd H2s, fourth at the bottom).

All values that reach the shell go through single-quote escaping; post content and images are transferred
as files via SFTP rather than inlined into commands.

## Development

```bash
dotnet test WPAIPoster.sln     # run the unit tests
```

The codebase is structured so the AI, image, and WordPress layers can be tested without a live model or
server (see `WPAIPoster.Tests/`). For deeper architectural notes, see [`CLAUDE.md`](./CLAUDE.md).

## Notes & limitations

- Video upload isn't implemented yet (images only).
- The post structure (meta title/description, H1, H2/H3 flow, CTA, internal links, image themes, tags,
  categories) is produced as structured JSON by the model and assembled deterministically by the app.
- SSH uses host-key verification (pin or trust-on-first-use); the first connection to a new server pins
  its key into `ssh-config.json`.
- Tested against a fake SSH runner; a real end-to-end run needs your server and WP-CLI configured.
