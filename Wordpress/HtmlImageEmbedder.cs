using System.Text;

namespace WPAIPoster.Wordpress;

/// <summary>An uploaded image ready to embed in post HTML.</summary>
public sealed record EmbeddedImage(string Url, string Alt);

/// <summary>
/// Inserts <c>&lt;figure&gt;&lt;img&gt;</c> blocks into post body HTML at structural positions:
/// the first (best) image under the H1 (top of body), the next two under the 2nd and 3rd <c>&lt;h2&gt;</c>
/// headings, and any further image at the bottom. <paramref name="images"/> must be ordered best-first.
/// </summary>
public static class HtmlImageEmbedder
{
    private const string H2Close = "</h2>";

    public static string Embed(string bodyHtml, IReadOnlyList<EmbeddedImage> images)
    {
        if (images.Count == 0)
            return bodyHtml;

        // Offsets just after each "</h2>" — i.e. directly under that heading.
        var h2Ends = new List<int>();
        int idx = 0;
        while ((idx = bodyHtml.IndexOf(H2Close, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            idx += H2Close.Length;
            h2Ends.Add(idx);
        }

        // Target offsets in image order: under H1 (top), under 2nd H2, under 3rd H2, then bottom.
        var targets = new List<int> { 0 };
        if (h2Ends.Count >= 2) targets.Add(h2Ends[1]);
        if (h2Ends.Count >= 3) targets.Add(h2Ends[2]);
        targets.Add(bodyHtml.Length);

        // Assign each image to a target; images beyond the target list pile up at the bottom.
        var insertions = new List<(int Offset, string Html)>();
        for (int i = 0; i < images.Count; i++)
        {
            int offset = i < targets.Count ? targets[i] : bodyHtml.Length;
            insertions.Add((offset, Figure(images[i])));
        }

        // Weave images into the body at ascending offsets (OrderBy is stable, so equal offsets keep order).
        var sb = new StringBuilder(bodyHtml.Length + images.Count * 96);
        int cursor = 0;
        foreach (var ins in insertions.OrderBy(x => x.Offset))
        {
            sb.Append(bodyHtml, cursor, ins.Offset - cursor);
            sb.Append(ins.Html);
            cursor = ins.Offset;
        }
        sb.Append(bodyHtml, cursor, bodyHtml.Length - cursor);
        return sb.ToString();
    }

    private static string Figure(EmbeddedImage img)
        => $"\n<figure><img src=\"{img.Url}\" alt=\"{EscapeAttr(img.Alt)}\" /></figure>\n";

    private static string EscapeAttr(string value)
        => value.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");
}
