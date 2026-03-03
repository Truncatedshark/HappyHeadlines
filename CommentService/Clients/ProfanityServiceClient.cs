namespace CommentService.Clients;

public class ProfanityServiceClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ProfanityServiceClient> _logger;

    public ProfanityServiceClient(HttpClient httpClient, ILogger<ProfanityServiceClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    // Returns null if the circuit is open or the service is unreachable
    public async Task<CheckResult?> CheckTextAsync(string text)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("/check", new { Text = text });
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<CheckResult>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("ProfanityService unavailable: {Message}", ex.Message);
            return null;
        }
    }
}

public record CheckResult(bool ContainsProfanity, List<string> MatchedWords);
