using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using WPAIPoster.Config;

namespace WPAIPoster.Images;

/// <summary>
/// Image preparation backed by ImageSharp: builds small thumbnails for vision scoring and
/// recompresses chosen images to stay under the per-image upload size cap.
/// </summary>
public static class ImagePreparer
{
    /// <summary>
    /// Loads <paramref name="path"/>, downscales it to a small thumbnail, and returns it as
    /// (base64 JPEG, "image/jpeg") suitable for a vision request.
    /// </summary>
    public static (string Base64, string MimeType) MakeVisionThumbnailBase64(
        string path, int maxDimension = AppLimits.VisionThumbnailDimension)
    {
        using var image = Image.Load(path);
        Downscale(image, maxDimension);

        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms, new JpegEncoder { Quality = 70 });
        return (Convert.ToBase64String(ms.ToArray()), "image/jpeg");
    }

    /// <summary>
    /// Recompresses <paramref name="sourcePath"/> to <paramref name="destPath"/> (JPEG) so the result
    /// is at or under <paramref name="maxBytes"/>, stepping quality down and downscaling as needed.
    /// Returns the final byte size written.
    /// </summary>
    public static long PrepareForUpload(
        string sourcePath,
        string destPath,
        long maxBytes = AppLimits.MaxImageBytes,
        int maxDimension = AppLimits.MaxImageDimension)
    {
        using var image = Image.Load(sourcePath);
        Downscale(image, maxDimension);

        // Try decreasing quality first; if still too big, downscale and retry.
        int[] qualities = { 85, 75, 65, 55, 45, 35 };
        byte[]? best = null;

        for (int round = 0; round < 5; round++)
        {
            foreach (int q in qualities)
            {
                using var ms = new MemoryStream();
                image.SaveAsJpeg(ms, new JpegEncoder { Quality = q });
                byte[] bytes = ms.ToArray();
                best = bytes;
                if (bytes.LongLength <= maxBytes)
                {
                    File.WriteAllBytes(destPath, bytes);
                    return bytes.LongLength;
                }
            }

            // Still too large — halve the longest side and try again.
            int next = Math.Max(64, Math.Max(image.Width, image.Height) / 2);
            Downscale(image, next);
        }

        // Give up shrinking further; write the smallest we produced.
        best ??= Array.Empty<byte>();
        File.WriteAllBytes(destPath, best);
        return best.LongLength;
    }

    private static void Downscale(Image image, int maxDimension)
    {
        int longest = Math.Max(image.Width, image.Height);
        if (longest <= maxDimension)
            return;

        double scale = (double)maxDimension / longest;
        int w = Math.Max(1, (int)Math.Round(image.Width * scale));
        int h = Math.Max(1, (int)Math.Round(image.Height * scale));
        image.Mutate(x => x.Resize(w, h));
    }
}
