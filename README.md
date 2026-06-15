# WPAIPoster

Generate a complete, SEO-optimised **WordPress blog post** from a one-line brief — written by AI,
illustrated with images from your own library, and published to your site over SSH using WP-CLI.

By default it uses **LM Studio** running locally (your content never leaves your machine), but you can
point it at **Anthropic**, **OpenAI**, or **Ollama** instead. Posts are created as **drafts** unless you
ask to publish.

## What it does

1. You give it a short brief (a sentence or two about the post you want).
2. It reads your site's existing published posts so it can suggest **real internal links**.
3. The AI writes the post — meta title, meta description, H1, scannable body with H2/H3 sections, and a
   call to action — and returns it as structured data.
4. It scans your local **image library**, uses a vision model to score which images best match the post,
   picks the best few, resizes them under 500 KB, and marks one as the **featured image**.
5. It connects to your WordPress server over SSH, uploads everything, and creates the post via WP-CLI —
   draft by default.

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
  "maxImagesToScore": 60,
  "imagesPerPost": 3,
  "seoMetaKeys": {
    "title": "_yoast_wpseo_title",
    "description": "_yoast_wpseo_metadesc"
  }
}
```

| Setting | Meaning |
|---|---|
| `provider` | `lmstudio` (default), `ollama`, `openai`, `openai-compatible`, or `anthropic`. |
| `model` | Model used to write the post. |
| `visionModel` | Vision-capable model used to score images (falls back to `model`). |
| `baseUrl` | Endpoint for local/compatible providers. |
| `apiKey` | Cloud key. Leave `null` and use `ANTHROPIC_API_KEY` / `OPENAI_API_KEY` env vars instead. |
| `imageLibrary` | Local folder scanned for images. |
| `autoPublish` | `false` → draft (default). `true` → publish immediately. |
| `wordPressFolder` | Folder on the server containing the WordPress install (where `wp` runs). |
| `maxImagesToScore` | Cap on how many library images are sent to the vision model. |
| `imagesPerPost` | How many images to attach (including the featured one). |
| `seoMetaKeys` | Post-meta keys for your SEO plugin (defaults to Yoast). Set to `null` to skip. |

### 2. Configure SSH — `ssh-config.json`

```json
{
  "server": "your.server.com",
  "port": 22,
  "username": "you",
  "keyPath": "../id_rsa",
  "privateKeyPwdEnc": null,
  "passwordEnc": null
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

Secrets are encrypted with **AES-256-GCM** using a key kept in a local `ssh-config.key` file
(permissions `0600` on macOS/Linux). That key file — along with `id_rsa` and other key material — is
gitignored and must never be committed.

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

If you don't pass a brief on the command line, the app prompts you for one.

### Options

| Flag | Effect |
|---|---|
| `--publish` | Publish immediately (overrides `autoPublish`). |
| `--draft` | Force draft (overrides `autoPublish`). |
| `--no-images` | Skip image selection and upload. |
| `--set-key-password` | Encrypt and store the private-key passphrase. |
| `--set-ssh-password` | Encrypt and store the SSH login password. |
| `-h`, `--help` | Show help. |

When it finishes, it prints the new post ID and an admin edit URL.

## How publishing works (under the hood)

The app runs standard WP-CLI commands in your `wordPressFolder`:

- `wp post list` — to gather existing posts for internal-link suggestions.
- `wp post create <file> --post_status=draft|publish --post_excerpt=… --porcelain` — the post body is
  uploaded as a file (via SFTP) and passed to WP-CLI, so HTML is never mangled by shell escaping.
- `wp post meta update …` — writes the SEO meta title/description (if `seoMetaKeys` is set).
- `wp media import <file> --post_id=… [--featured_image] --porcelain` — uploads each image and attaches it.
- `wp post update <id> <file>` — re-saves the body with the uploaded image URLs embedded inline.

## Development

```bash
dotnet test WPAIPoster.sln     # run the unit tests
```

The codebase is structured so the AI, image, and WordPress layers can be tested without a live model or
server (see `WPAIPoster.Tests/`). For deeper architectural notes, see [`CLAUDE.md`](./CLAUDE.md).

## Notes & limitations

- Video upload isn't implemented yet (images only).
- The post structure (meta title/description, H1, H2/H3 flow, CTA, internal links, image themes) is
  produced as structured JSON by the model and assembled deterministically by the app.
- Tested against a fake SSH runner; a real end-to-end run needs your server and WP-CLI configured.
