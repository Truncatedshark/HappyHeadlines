public static class PublisherEndpoints
{
    public static void MapPublisherEndpoints(this WebApplication app)
    {
        app.MapPost("/publish", (PublishRequest request, ArticleQueuePublisher publisher, ILogger<PublishRequest> logger) =>
        {
            var message = new ArticleMessage
            {
                Id = Guid.NewGuid(),
                Title = request.Title,
                Content = request.Content,
                Author = request.Author,
                Region = request.Region,
                PublishedAt = DateTime.UtcNow
            };

            publisher.Publish(message);

            logger.LogInformation(
                "Article {ArticleId} published to queue by {Author} for region {Region}",
                message.Id, message.Author, message.Region);

            return Results.Accepted($"/articles/{message.Id}", new { message.Id, message.Region });
        });
    }
}
