namespace WPAIPoster.Images;

/// <summary>Common words ignored when matching post content to image tags.</summary>
public static class StopWords
{
    public static readonly IReadOnlySet<string> Set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "or", "but", "if", "then", "else", "for", "of", "to", "in", "on",
        "at", "by", "with", "from", "as", "is", "are", "was", "were", "be", "been", "being", "it",
        "its", "this", "that", "these", "those", "you", "your", "we", "our", "they", "their", "he",
        "she", "his", "her", "i", "me", "my", "us", "them", "not", "no", "yes", "so", "than", "too",
        "very", "can", "will", "just", "should", "would", "could", "may", "might", "must", "do",
        "does", "did", "have", "has", "had", "about", "into", "over", "under", "out", "up", "down",
        "more", "most", "some", "any", "all", "each", "how", "what", "when", "where", "which", "who",
        "why", "there", "here", "also", "such", "only", "own", "same", "other", "get", "got", "use",
    };

    public static bool IsStopWord(string token) => Set.Contains(token);
}
