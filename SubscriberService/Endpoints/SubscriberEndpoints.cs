using Microsoft.EntityFrameworkCore;
using SubscriberService.Data;
using SubscriberService.Messaging;
using SubscriberService.Models;

namespace SubscriberService.Endpoints;

public static class SubscriberEndpoints
{
    public static void MapSubscriberEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/subscribers");

        // POST /subscribers — subscribe a new email address
        group.MapPost("/", async (SubscribeRequest request, SubscriberDbContext db, SubscriberQueuePublisher publisher) =>
        {
            if (await db.Subscribers.AnyAsync(s => s.Email == request.Email))
                return Results.Conflict("Email is already subscribed.");

            var subscriber = new Subscriber { Email = request.Email };
            db.Subscribers.Add(subscriber);
            await db.SaveChangesAsync();

            // Put the new subscriber on the queue so NewsletterService sends a welcome mail
            publisher.Publish(subscriber);

            return Results.Created($"/subscribers/{subscriber.Id}", subscriber);
        });

        // DELETE /subscribers/{email} — unsubscribe
        group.MapDelete("/{email}", async (string email, SubscriberDbContext db) =>
        {
            var subscriber = await db.Subscribers.FirstOrDefaultAsync(s => s.Email == email);
            if (subscriber is null) return Results.NotFound();

            db.Subscribers.Remove(subscriber);
            await db.SaveChangesAsync();

            return Results.NoContent();
        });

        // GET /subscribers — list all subscribers (called by NewsletterService)
        group.MapGet("/", async (SubscriberDbContext db) =>
            Results.Ok(await db.Subscribers.ToListAsync()));
    }
}

public record SubscribeRequest(string Email);
