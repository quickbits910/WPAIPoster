namespace WPAIPoster.Llm;

public interface ILlmClient
{
    Task<string?> SendAsync(string promptText, string? base64Image, string? mimeType);
    Task<string?> SendAsync(string promptText, IReadOnlyList<(string Base64, string MimeType)> images);
}
