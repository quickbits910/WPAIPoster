using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace WPAIPoster.Images;

/// <summary>
/// Difference hash (dHash): a 64-bit perceptual fingerprint of an image. Two images whose hashes are
/// within a small Hamming distance are visually near-identical — used to avoid attaching
/// duplicate-looking images to a post (common with blocks of AI-generated library images).
/// </summary>
public static class PerceptualHash
{
    // A 9x8 grayscale grid yields 8 horizontal "is-brighter-than-right-neighbour" comparisons per row,
    // i.e. 8 rows x 8 comparisons = 64 bits.
    private const int Width = 9;
    private const int Height = 8;

    /// <summary>Computes the dHash of the image at <paramref name="path"/>.</summary>
    public static ulong Compute(string path)
    {
        using var image = Image.Load<L8>(path);
        image.Mutate(x => x.Resize(Width, Height));
        return Compute(image);
    }

    /// <summary>
    /// Computes the dHash of image bytes read from <paramref name="stream"/> — used for images fetched
    /// over HTTP (e.g. recent posts' featured images) that never touch the local disk.
    /// </summary>
    public static ulong Compute(Stream stream)
    {
        using var image = Image.Load<L8>(stream);
        image.Mutate(x => x.Resize(Width, Height));
        return Compute(image);
    }

    /// <summary>True when <paramref name="hash"/> is within <paramref name="threshold"/> bits of any hash in <paramref name="others"/>.</summary>
    public static bool IsWithinAny(ulong hash, IEnumerable<ulong> others, int threshold)
        => others.Any(o => HammingDistance(hash, o) <= threshold);

    /// <summary>Computes the dHash of an already-loaded 9x8 8-bit grayscale image.</summary>
    internal static ulong Compute(Image<L8> image)
    {
        ulong hash = 0;
        int bit = 0;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width - 1; x++)
            {
                byte left = image[x, y].PackedValue;
                byte right = image[x + 1, y].PackedValue;
                if (left > right) hash |= 1UL << bit;
                bit++;
            }
        }
        return hash;
    }

    /// <summary>Number of differing bits between two hashes (0 = identical, 64 = opposite).</summary>
    public static int HammingDistance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);
}
