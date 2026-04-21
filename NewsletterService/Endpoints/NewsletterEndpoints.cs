using NewsletterService.Clients;

namespace NewsletterService.Endpoints;

public static class NewsletterEndpoints
{
    public static void MapNewsletterEndpoints(this WebApplication app)
    {
        // POST /newsletters/send — fetch all subscribers and send newsletter to each.
        // If SubscriberService is unreachable (circuit open), returns 503.
        app.MapPost("/newsletters/send", async (SubscriberServiceClient client, ILogger<Program> logger) =>
        {
            var subscribers = await client.GetSubscribersAsync();

            if (subscribers is null)
                return Results.StatusCode(503); // SubscriberService is down — fault isolated

            if (subscribers.Count == 0)
                return Results.Ok(new { message = "No subscribers to send to." });

            foreach (var subscriber in subscribers)
            {
                // In a real system: call SMTP / mail provider here.
                logger.LogInformation("Sending newsletter to {Email}", subscriber.Email);
            }

            return Results.Ok(new { message = $"Newsletter sent to {subscribers.Count} subscriber(s)." });
        });
    }
}
