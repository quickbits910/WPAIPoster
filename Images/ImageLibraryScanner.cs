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
}
