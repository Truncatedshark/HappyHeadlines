namespace ArticleService.Models;

// Mirrors the message shape published by PublisherService onto the articles queue.
public class ArticleMessage
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public DateTime PublishedAt { get; set; }
}
