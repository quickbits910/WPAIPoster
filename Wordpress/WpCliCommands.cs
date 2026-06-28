namespace WPAIPoster.Wordpress;

/// <summary>
/// Pure builders for the WP-CLI command strings used by <see cref="WpCliPublisher"/>.
/// Kept side-effect free so they can be unit-tested without a server. All user-supplied values are
/// single-quoted for POSIX shells.
/// </summary>
public static class WpCliCommands
{
    /// <summary>POSIX single-quote escaping: wrap in '...' and escape embedded quotes as '\''.</summary>
    public static string ShellQuote(string? value)
    {
        value ??= string.Empty;
        return "'" + value.Replace("'", "'\\''") + "'";
    }

    /// <summary>Prefixes <paramref name="command"/> with a <c>cd</c> into the WordPress folder.</summary>
    public static string InFolder(string wordPressFolder, string command)
        => $"cd {ShellQuote(wordPressFolder)} && {command}";

    /// <summary>Lists published posts as JSON for internal-link context.</summary>
    public static string ListPublishedPosts(int limit = 200)
        => $"wp post list --post_status=publish --fields=ID,post_title,url --format=json --posts_per_page={limit}";

    /// <summary>Lists the IDs of the most recently published posts (newest first) as a JSON array.</summary>
    public static string ListRecentPublishedPostIds(int limit)
        => $"wp post list --post_status=publish --field=ID --format=json "
         + $"--orderby=date --order=DESC --posts_per_page={limit}";

    /// <summary>Reads a single post meta value (e.g. <c>_thumbnail_id</c> for the featured attachment).</summary>
    public static string GetPostMeta(int postId, string key)
        => $"wp post meta get {postId} {ShellQuote(key)}";

    /// <summary>
    /// Creates a post from a remote content file. Returns the new ID via <c>--porcelain</c>.
    /// <paramref name="publish"/> false ⇒ draft (the default).
    /// </summary>
    public static string CreatePost(string remoteContentPath, string title, bool publish, string excerpt)
    {
        string status = publish ? "publish" : "draft";
        return $"wp post create {ShellQuote(remoteContentPath)} --post_type=post "
             + $"--post_title={ShellQuote(title)} --post_status={status} "
             + $"--post_excerpt={ShellQuote(excerpt)} --porcelain";
    }

    /// <summary>Replaces a post's content from a remote content file.</summary>
    public static string UpdatePostContent(int postId, string remoteContentPath)
        => $"wp post update {postId} {ShellQuote(remoteContentPath)}";

    /// <summary>Sets a single post meta value (used for SEO plugin meta title/description).</summary>
    public static string UpdateMeta(int postId, string key, string value)
        => $"wp post meta update {postId} {ShellQuote(key)} {ShellQuote(value)}";

    /// <summary>
    /// Imports a local-on-remote image, attaches it to <paramref name="postId"/>, and optionally marks
    /// it as the featured image. Returns the attachment ID via <c>--porcelain</c>.
    /// </summary>
    public static string ImportMedia(string remoteImagePath, int postId, string title, string alt, bool featured)
    {
        string cmd = $"wp media import {ShellQuote(remoteImagePath)} --post_id={postId} "
                   + $"--title={ShellQuote(title)} --alt={ShellQuote(alt)}";
        if (featured)
            cmd += " --featured_image";
        return cmd + " --porcelain";
    }

    /// <summary>Reads an attachment's URL (its <c>guid</c>) so it can be embedded in the post body.</summary>
    public static string GetAttachmentUrl(int attachmentId)
        => $"wp post get {attachmentId} --field=guid";

    /// <summary>Creates a taxonomy term by name, returning its ID (<c>--porcelain</c>). Errors if it exists.</summary>
    public static string CreateTerm(string taxonomy, string name)
        => $"wp term create {taxonomy} {ShellQuote(name)} --porcelain";

    /// <summary>Looks up an existing term's ID by exact name (used when creation reports it already exists).</summary>
    public static string GetTermId(string taxonomy, string name)
        => $"wp term list {taxonomy} --name={ShellQuote(name)} --field=term_id";

    /// <summary>
    /// Sets (replaces) a post's terms for a taxonomy by term ID. <c>wp post term set --by</c> only accepts
    /// <c>slug</c> or <c>id</c>, so terms are resolved to IDs first.
    /// </summary>
    public static string SetPostTerms(int postId, string taxonomy, IEnumerable<int> termIds)
        => $"wp post term set {postId} {taxonomy} {string.Join(" ", termIds)} --by=id";

    /// <summary>Deletes a remote temp file created during publishing.</summary>
    public static string RemoveRemoteFile(string remotePath)
        => $"rm -f {ShellQuote(remotePath)}";
}
