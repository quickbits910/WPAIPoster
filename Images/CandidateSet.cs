namespace WPAIPoster.Images;

/// <summary>Builds the vision-scoring candidate set: tag-selected images first, topped up with fill images.</summary>
public static class CandidateSet
{
    /// <summary>
    /// Returns <paramref name="primary"/> (tag picks, in order) followed by <paramref name="fill"/> images
    /// (newest) not already included, deduped, capped at <paramref name="max"/>.
    /// </summary>
    public static IReadOnlyList<string> Build(
        IReadOnlyList<string> primary, IReadOnlyList<string> fill, int max)
    {
        var result = new List<string>();
        if (max <= 0)
            return result;

        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddFrom(IReadOnlyList<string> source)
        {
            foreach (string path in source)
            {
                if (result.Count >= max) return;
                if (seen.Add(path)) result.Add(path);
            }
        }

        AddFrom(primary);
        AddFrom(fill);
        return result;
    }
}
