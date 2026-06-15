using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Iptc;

namespace WPAIPoster.Images;

/// <summary>Reads keyword tags previously written to an image (by ImageTagger or similar).</summary>
public interface IImageTagReader
{
    /// <summary>Returns the distinct keyword tags found on <paramref name="path"/> (raw, no prefix stripping).</summary>
    IReadOnlyList<string> ReadTags(string path);
}

/// <summary>
/// Reads the union of keyword tags from an image across three backends ImageTagger writes to:
/// XMP <c>dc:subject</c>, IPTC keywords, and the Linux xattr <c>user.xdg.tags</c>. Metadata is read via
/// <see cref="Image.Identify(string)"/> (no pixel decode) for speed, falling back to <see cref="Image.Load(string)"/>.
/// Results are deduped case-insensitively. xattr is read only on Linux.
/// </summary>
public sealed class ImageTagReader : IImageTagReader
{
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

    public IReadOnlyList<string> ReadTags(string path)
    {
        var tags = new List<string>();

        try
        {
            ImageMetadataReadInto(path, tags);
        }
        catch
        {
            // Unreadable/undecodable image metadata — ignore, fall through to xattr.
        }

        try
        {
            tags.AddRange(ReadXattrTags(path));
        }
        catch
        {
            // xattr unavailable on this platform/filesystem — ignore.
        }

        // Distinct, case-insensitive, preserve first-seen casing and order.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (string t in tags)
        {
            string trimmed = t.Trim();
            if (trimmed.Length > 0 && seen.Add(trimmed))
                result.Add(trimmed);
        }
        return result;
    }

    /// <summary>Reads XMP dc:subject and IPTC keywords using header-only Identify, falling back to Load.</summary>
    private static void ImageMetadataReadInto(string path, List<string> tags)
    {
        ImageInfo info = Image.Identify(path);
        bool any = AddIptc(info.Metadata.IptcProfile, tags) | AddXmp(info.Metadata.XmpProfile, tags);

        if (any)
            return;

        // Some encoders/formats don't surface profiles via Identify; do a full load as a fallback.
        using Image image = Image.Load(path);
        AddIptc(image.Metadata.IptcProfile, tags);
        AddXmp(image.Metadata.XmpProfile, tags);
    }

    private static bool AddIptc(IptcProfile? iptc, List<string> tags)
    {
        if (iptc is null) return false;
        bool added = false;
        foreach (var v in iptc.GetValues(IptcTag.Keywords))
        {
            if (!string.IsNullOrWhiteSpace(v.Value)) { tags.Add(v.Value); added = true; }
        }
        return added;
    }

    private static bool AddXmp(SixLabors.ImageSharp.Metadata.Profiles.Xmp.XmpProfile? xmp, List<string> tags)
    {
        byte[]? bytes = xmp?.ToByteArray();
        if (bytes is null) return false;

        try
        {
            using var ms = new MemoryStream(bytes);
            XDocument doc = XDocument.Load(ms);
            var items = doc.Descendants(Dc + "subject")
                           .Descendants(Rdf + "li")
                           .Select(e => e.Value)
                           .Where(s => !string.IsNullOrWhiteSpace(s))
                           .ToList();
            tags.AddRange(items);
            return items.Count > 0;
        }
        catch
        {
            return false; // malformed XMP packet
        }
    }

    // ---- Linux xattr (user.xdg.tags) ----

    [DllImport("libc", SetLastError = true)]
    private static extern nint getxattr(string path, string name, byte[]? value, nint size);

    private static IReadOnlyList<string> ReadXattrTags(string path)
    {
        if (!OperatingSystem.IsLinux())
            return Array.Empty<string>();

        nint size = getxattr(path, "user.xdg.tags", null, 0);
        if (size <= 0)
            return Array.Empty<string>();

        byte[] buf = new byte[(int)size];
        nint read = getxattr(path, "user.xdg.tags", buf, size);
        if (read <= 0)
            return Array.Empty<string>();

        return Encoding.UTF8.GetString(buf, 0, (int)read)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
