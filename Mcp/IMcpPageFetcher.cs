namespace WPAIPoster.Mcp;

public interface IMcpPageFetcher
{
    Task<string?> FetchContentAsync(string url, int maxLength);
}
