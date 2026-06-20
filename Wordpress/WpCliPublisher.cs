using System.Globalization;
using WPAIPoster.BlogPost;
using WPAIPoster.Config;
using WPAIPoster.Images;

namespace WPAIPoster.Wordpress;

/// <summary>Outcome of publishing a post.</summary>
public sealed record PublishOutcome(int PostId, bool Published, string? AdminEditUrl);

/// <summary>
/// Drives the WP-CLI publish flow over an <see cref="ISshRunner"/>: upload body → create post →
/// write SEO meta → upload &amp; import images (featured + inline) → embed image URLs → update post.
/// Every command runs inside the configured WordPress folder.
/// </summary>
public sealed class WpCliPublisher(
    ISshRunner runner, string wordPressFolder, SeoMetaKeys? seoMetaKeys, string defaultCategory = "Blog")
{
    private const string RemoteTmpDir = "/tmp";
    private const string TagTaxonomy = "post_tag";
    private const string CategoryTaxonomy = "category";

    /// <summary>
    /// Publishes <paramref name="post"/> with the selected <paramref name="images"/> (already prepared
    /// under the size cap). <paramref name="publish"/> false ⇒ draft.
    /// </summary>
    public PublishOutcome Publish(BlogPostResult post, IReadOnlyList<SelectedImage> images, bool publish)
    {
        string runId = Guid.NewGuid().ToString("N");
        var remoteTemps = new List<string>();

        try
        {
            // 1. Upload the body and create the post.
            string remoteBody = RemotePath($"wpaiposter-{runId}.html");
            remoteTemps.Add(remoteBody);
            UploadText(post.BodyHtml, remoteBody);

            string createCmd = WpCliCommands.InFolder(
                wordPressFolder,
                WpCliCommands.CreatePost(remoteBody, post.H1, publish, post.MetaDescription));
            int postId = RunForId(createCmd, "create post");

            // 2. SEO meta (optional, plugin-dependent).
            WriteSeoMeta(postId, post);

            // 2b. Tags (max 5) and categories (default applied when none).
            ApplyTerms(postId, TagTaxonomy, post.Tags.Take(AppLimits.MaxPostTags));
            var categories = post.Categories.Count > 0 ? post.Categories : new List<string> { defaultCategory };
            ApplyTerms(postId, CategoryTaxonomy, categories);

            // 3. Upload + import each image; collect URLs for inline embedding.
            var embedded = new List<EmbeddedImage>();
            for (int i = 0; i < images.Count; i++)
            {
                SelectedImage img = images[i];
                string ext = Path.GetExtension(img.Path);
                string remoteImg = RemotePath($"wpaiposter-{runId}-{i}{ext}");
                remoteTemps.Add(remoteImg);
                runner.UploadFile(img.Path, remoteImg);

                string title = $"{post.H1} image {i + 1}";
                string alt = post.ImageThemes.Count > 0
                    ? string.Join(", ", post.ImageThemes.Select(t => t.Subject))
                    : post.H1;
                string importCmd = WpCliCommands.InFolder(
                    wordPressFolder,
                    WpCliCommands.ImportMedia(remoteImg, postId, title, alt, img.IsFeatured));
                int attId = RunForId(importCmd, "import media");

                string urlCmd = WpCliCommands.InFolder(wordPressFolder, WpCliCommands.GetAttachmentUrl(attId));
                string url = runner.Run(urlCmd).TrimmedOut;

                // Embed every image inline at a structural position (best-first → under H1, 2nd/3rd H2, bottom).
                // The best image is still also set as the WordPress featured image above.
                if (url.Length > 0)
                    embedded.Add(new EmbeddedImage(url, alt));
            }

            // 4. Re-write the body with inline images, if any.
            if (embedded.Count > 0)
            {
                string finalHtml = HtmlImageEmbedder.Embed(post.BodyHtml, embedded);
                string remoteBody2 = RemotePath($"wpaiposter-{runId}-final.html");
                remoteTemps.Add(remoteBody2);
                UploadText(finalHtml, remoteBody2);

                string updateCmd = WpCliCommands.InFolder(
                    wordPressFolder, WpCliCommands.UpdatePostContent(postId, remoteBody2));
                Run(updateCmd, "update post content");
            }

            return new PublishOutcome(postId, publish, BuildAdminUrl(postId));
        }
        finally
        {
            foreach (string tmp in remoteTemps)
            {
                try { runner.Run(WpCliCommands.RemoveRemoteFile(tmp)); }
                catch { /* best-effort cleanup */ }
            }
        }
    }

    private void WriteSeoMeta(int postId, BlogPostResult post)
    {
        if (seoMetaKeys is null)
            return;

        if (!string.IsNullOrWhiteSpace(seoMetaKeys.Title) && post.MetaTitle.Length > 0)
            Run(WpCliCommands.InFolder(wordPressFolder,
                WpCliCommands.UpdateMeta(postId, seoMetaKeys.Title!, post.MetaTitle)), "set SEO title");

        if (!string.IsNullOrWhiteSpace(seoMetaKeys.Description) && post.MetaDescription.Length > 0)
            Run(WpCliCommands.InFolder(wordPressFolder,
                WpCliCommands.UpdateMeta(postId, seoMetaKeys.Description!, post.MetaDescription)), "set SEO description");
    }

    /// <summary>
    /// Resolves each name to a term ID (creating it if missing), then assigns the IDs to the post.
    /// Empty/duplicate names and unresolvable terms are dropped; nothing is assigned when none remain.
    /// </summary>
    private void ApplyTerms(int postId, string taxonomy, IEnumerable<string> names)
    {
        var clean = names
            .Select(n => n.Trim())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (clean.Count == 0)
            return;

        var ids = new List<int>();
        foreach (string name in clean)
        {
            int? id = ResolveTermId(taxonomy, name);
            if (id is int value && !ids.Contains(value))
                ids.Add(value);
        }
        if (ids.Count == 0)
            return;

        Run(WpCliCommands.InFolder(wordPressFolder, WpCliCommands.SetPostTerms(postId, taxonomy, ids)),
            $"set {taxonomy} terms");
    }

    /// <summary>Creates the term (returning its new ID) or, if it already exists, looks the ID up by name.</summary>
    private int? ResolveTermId(string taxonomy, string name)
    {
        SshCommandResult create = runner.Run(WpCliCommands.InFolder(wordPressFolder,
            WpCliCommands.CreateTerm(taxonomy, name)));
        if (create.Success && TryFirstInt(create.TrimmedOut, out int created))
            return created;

        SshCommandResult list = runner.Run(WpCliCommands.InFolder(wordPressFolder,
            WpCliCommands.GetTermId(taxonomy, name)));
        return TryFirstInt(list.TrimmedOut, out int existing) ? existing : null;
    }

    private static bool TryFirstInt(string text, out int value)
    {
        foreach (string token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                return true;
        value = 0;
        return false;
    }

    private string? BuildAdminUrl(int postId)
    {
        try
        {
            string siteUrl = runner.Run(WpCliCommands.InFolder(wordPressFolder, "wp option get siteurl")).TrimmedOut;
            if (siteUrl.Length == 0)
                return null;
            return $"{siteUrl.TrimEnd('/')}/wp-admin/post.php?post={postId}&action=edit";
        }
        catch
        {
            return null;
        }
    }

    private void UploadText(string content, string remotePath)
    {
        string local = Path.Combine(Path.GetTempPath(), Path.GetFileName(remotePath));
        File.WriteAllText(local, content);
        try { runner.UploadFile(local, remotePath); }
        finally { try { File.Delete(local); } catch { /* ignore */ } }
    }

    private SshCommandResult Run(string command, string what)
    {
        SshCommandResult r = runner.Run(command);
        if (!r.Success)
            throw new InvalidOperationException($"WP-CLI failed to {what} (exit {r.ExitCode}): {r.StdErr.Trim()}");
        return r;
    }

    private int RunForId(string command, string what)
    {
        SshCommandResult r = Run(command, what);
        string outText = r.TrimmedOut;
        // Porcelain output may include trailing notices; take the first integer token.
        foreach (string token in outText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                return id;

        throw new InvalidOperationException($"WP-CLI did not return an ID after {what}: '{outText}'.");
    }

    private static string RemotePath(string fileName) => $"{RemoteTmpDir}/{fileName}";
}
