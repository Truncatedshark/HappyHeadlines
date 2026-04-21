using System.Net.Http.Json;
using NewsletterService.Models;

namespace NewsletterService.Clients;

public class SubscriberServiceClient
{
    private readonly HttpClient _http;
    private readonly ILogger<SubscriberServiceClient> _logger;

    public SubscriberServiceClient(HttpClient http, ILogger<SubscriberServiceClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    // Returns null when the circuit is open (SubscriberService is down).
    // Callers must handle null gracefully — this is the fault isolation boundary.
    public async Task<List<SubscriberMessage>?> GetSubscribersAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<List<SubscriberMessage>>("/subscribers");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not reach SubscriberService — circuit may be open");
            return null;
        }
    }
}
