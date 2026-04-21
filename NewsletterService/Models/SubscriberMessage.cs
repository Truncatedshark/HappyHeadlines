namespace NewsletterService.Models;

// Mirrors the Subscriber model from SubscriberService.
// NewsletterService owns its own copy so it has no compile-time dependency on SubscriberService.
public class SubscriberMessage
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public DateTime SubscribedAt { get; set; }
}
