namespace WPAIPoster.Images;

/// <summary>An image with its (prefix-stripped, distinct) keyword tags and last-modified time.</summary>
public sealed record TaggedImage(string Path, IReadOnlyList<string> Tags, DateTime ModifiedUtc);

/// <summary>
/// The indexed image library: tagged entries (newest-first) plus a fast tag→paths lookup. Tags are
/// stored with the configured prefix already stripped; the lookup keys are lower-cased for matching.
/// </summary>
public sealed class ImageTagCatalog
{
    /// <summary>All indexed images, newest first. Entries may have empty <see cref="TaggedImage.Tags"/>.</summary>
    public IReadOnlyList<TaggedImage> Images { get; }

    /// <summary>Lower-cased tag → image paths that carry it. Fast membership/lookup for matching.</summary>
    public IReadOnlyDictionary<string, List<string>> TagIndex { get; }

    public ImageTagCatalog(IReadOnlyList<TaggedImage> images)
    {
        Images = images;

        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (TaggedImage img in images)
        {
            foreach (string tag in img.Tags)
            {
                string key = tag.ToLowerInvariant();
                if (!index.TryGetValue(key, out List<string>? paths))
                    index[key] = paths = new List<string>();
                paths.Add(img.Path);
            }
        }
        TagIndex = index;
    }

    /// <summary>Paths newest-first (the indexing order), used to top up candidate sets.</summary>
    public IReadOnlyList<string> NewestPaths => Images.Select(i => i.Path).ToList();

    /// <summary>Total distinct images that carry at least one tag.</summary>
    public int TaggedCount => Images.Count(i => i.Tags.Count > 0);
}
