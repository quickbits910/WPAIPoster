using System.Text;

namespace WPAIPoster.Wordpress;

/// <summary>An uploaded image ready to embed in post HTML.</summary>
public sealed record EmbeddedImage(string Url, string Alt);

/// <summary>Inserts <c>&lt;figure&gt;&lt;img&gt;</c> blocks into post body HTML, distributed across paragraphs.</summary>
public static class HtmlImageEmbedder
{
    private const string ParaClose = "</p>";

    /// <summary>
    /// Distributes <paramref name="images"/> through <paramref name="bodyHtml"/> by inserting each after a
    /// paragraph break. Falls back to appending at the end when there are no paragraphs.
    /// </summary>
    public static string Embed(string bodyHtml, IReadOnlyList<EmbeddedImage> images)
    {
        if (images.Count == 0)
            return bodyHtml;

        // Locate the positions just after each "</p>".
        var insertPoints = new List<int>();
        int idx = 0;
        while ((idx = bodyHtml.IndexOf(ParaClose, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            idx += ParaClose.Length;
            insertPoints.Add(idx);
        }

        if (insertPoints.Count == 0)
        {
            var sb0 = new StringBuilder(bodyHtml);
            foreach (EmbeddedImage img in images)
                sb0.Append(Figure(img));
            return sb0.ToString();
        }

        // Map each image to a paragraph gap, spread across the body, and insert from the end so
        // earlier offsets stay valid.
        var insertions = new List<(int Pos, string Html)>();
        for (int i = 0; i < images.Count; i++)
        {
            int slot = (int)Math.Floor((i + 1) * insertPoints.Count / (double)(images.Count + 1));
            slot = Math.Clamp(slot, 0, insertPoints.Count - 1);
            insertions.Add((insertPoints[slot], Figure(images[i])));
        }

        var result = new StringBuilder(bodyHtml);
        foreach (var ins in insertions.OrderByDescending(x => x.Pos))
            result.Insert(ins.Pos, ins.Html);

        return result.ToString();
    }

    private static string Figure(EmbeddedImage img)
        => $"\n<figure><img src=\"{img.Url}\" alt=\"{EscapeAttr(img.Alt)}\" /></figure>\n";

    private static string EscapeAttr(string value)
        => value.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");
}
