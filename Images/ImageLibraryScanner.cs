namespace WPAIPoster.Images;

/// <summary>Enumerates candidate images from the local image library.</summary>
public static class ImageLibraryScanner
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp"
    };

    /// <summary>
    /// Returns image file paths under <paramref name="libraryPath"/> (recursive), newest first,
    /// capped at <paramref name="max"/>. Returns empty when the path is missing.
    /// </summary>
    public static IReadOnlyList<string> Scan(string? libraryPath, int max)
    {
        if (string.IsNullOrWhiteSpace(libraryPath) || !Directory.Exists(libraryPath))
            return Array.Empty<string>();

        return Directory
            .EnumerateFiles(libraryPath, "*", SearchOption.AllDirectories)
            .Where(f => Extensions.Contains(Path.GetExtension(f)))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(Math.Max(0, max))
            .ToList();
    }

    public static bool IsSupported(string path) => Extensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Scans the library (newest-first, capped at <paramref name="maxIndex"/>) and reads each image's tags
    /// via <paramref name="reader"/>, stripping <paramref name="tagPrefix"/>, into an <see cref="ImageTagCatalog"/>.
    /// </summary>
    public static ImageTagCatalog ScanWithTags(
        string? libraryPath, int maxIndex, IImageTagReader reader, string? tagPrefix)
    {
        IReadOnlyList<string> paths = Scan(libraryPath, maxIndex);
        var entries = new List<TaggedImage>(paths.Count);

        foreach (string path in paths)
        {
            IReadOnlyList<string> tags = StripPrefix(reader.ReadTags(path), tagPrefix);
            entries.Add(new TaggedImage(path, tags, SafeModifiedUtc(path)));
        }

        return new ImageTagCatalog(entries);
    }

    /// <summary>Strips the configured prefix from each tag (case-insensitive) and dedupes.</summary>
    public static IReadOnlyList<string> StripPrefix(IReadOnlyList<string> tags, string? prefix)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (string tag in tags)
        {
            string t = tag;
            if (!string.IsNullOrEmpty(prefix) && t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                t = t[prefix.Length..];
            t = t.Trim();
            if (t.Length > 0 && seen.Add(t))
                result.Add(t);
        }
        return result;
    }

    private static DateTime SafeModifiedUtc(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }
}
