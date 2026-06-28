using System.Globalization;
using System.Text.Json;
using WPAIPoster.Images;

namespace WPAIPoster.Wordpress;

/// <summary>
/// Recovers the perceptual hashes (dHash) of recent published posts' featured images so the image
/// selector can avoid reusing the same hero image on consecutive posts.
/// <para>
/// The local→WordPress filename link is lost on upload (<c>wp media import</c> renames the file to a new
/// GUID), so identity is recovered by image <em>content</em> instead: list the most recent published
/// posts → read each <c>_thumbnail_id</c> → resolve the attachment URL → download it → hash it.
/// </para>
/// Every step is best-effort: posts with no featured image, failed lookups, and failed/undecodable
/// downloads are simply skipped (they contribute no hash), so a flaky fetch degrades gracefully instead
/// of aborting the run. The HTTP download is injected as a delegate so the orchestration is unit-testable
/// without a network.
/// </summary>
public sealed class FeaturedHistoryFetcher(
    ISshRunner runner, string wordPressFolder, Func<string, Stream?> downloadImage)
{
    private const string ThumbnailMetaKey = "_thumbnail_id";

    /// <summary>
    /// Returns the dHashes of up to <paramref name="count"/> recent posts' featured images, in no
    /// particular order. Returns an empty set when <paramref name="count"/> is non-positive or nothing
    /// could be fetched.
    /// </summary>
    public IReadOnlySet<ulong> FetchRecentFeaturedHashes(int count)
    {
        var hashes = new HashSet<ulong>();
        if (count <= 0)
            return hashes;

        foreach (int postId in ListRecentPostIds(count))
        {
            int? attachmentId = GetFeaturedAttachmentId(postId);
            if (attachmentId is null)
                continue;

            string url = GetAttachmentUrl(attachmentId.Value);
            if (url.Length == 0)
                continue;

            try
            {
                using Stream? stream = downloadImage(url);
                if (stream is not null)
                    hashes.Add(PerceptualHash.Compute(stream));
            }
            catch
            {
                // Network failure or undecodable image — skip; we just won't dedup against this one.
            }
        }

        return hashes;
    }

    private IReadOnlyList<int> ListRecentPostIds(int count)
    {
        SshCommandResult r = runner.Run(
            WpCliCommands.InFolder(wordPressFolder, WpCliCommands.ListRecentPublishedPostIds(count)));
        return r.Success ? ParsePostIds(r.StdOut) : Array.Empty<int>();
    }

    private int? GetFeaturedAttachmentId(int postId)
    {
        SshCommandResult r = runner.Run(
            WpCliCommands.InFolder(wordPressFolder, WpCliCommands.GetPostMeta(postId, ThumbnailMetaKey)));
        if (!r.Success)
            return null;
        // Empty output or "0" means the post has no featured image.
        return TryFirstPositiveInt(r.TrimmedOut, out int id) ? id : null;
    }

    private string GetAttachmentUrl(int attachmentId)
    {
        SshCommandResult r = runner.Run(
            WpCliCommands.InFolder(wordPressFolder, WpCliCommands.GetAttachmentUrl(attachmentId)));
        return r.Success ? r.TrimmedOut : string.Empty;
    }

    /// <summary>Parses a <c>wp post list --field=ID --format=json</c> array. Tolerant of numeric or string IDs.</summary>
    public static IReadOnlyList<int> ParsePostIds(string json)
    {
        var ids = new List<int>();
        if (string.IsNullOrWhiteSpace(json))
            return ids;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return ids;

            foreach (JsonElement el in doc.RootElement.EnumerateArray())
            {
                string raw = el.ValueKind switch
                {
                    JsonValueKind.Number => el.GetRawText(),
                    JsonValueKind.String => el.GetString() ?? string.Empty,
                    _ => string.Empty,
                };
                if (TryFirstPositiveInt(raw, out int id))
                    ids.Add(id);
            }
        }
        catch (JsonException)
        {
            // Unparseable output — treat as no posts.
        }

        return ids;
    }

    private static bool TryFirstPositiveInt(string text, out int value)
    {
        foreach (string token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value > 0)
                return true;
        value = 0;
        return false;
    }
}
