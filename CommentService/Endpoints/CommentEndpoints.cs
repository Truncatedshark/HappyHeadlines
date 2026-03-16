using CommentService.Caching;
using CommentService.Clients;
using CommentService.Data;
using CommentService.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CommentService.Endpoints;

public static class CommentEndpoints
{
    public static void MapCommentEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/comments");

        // POST /comments — create a comment; checks profanity or saves as Pending
        // Invalidates the cache for the affected article so stale data isn't served
        group.MapPost("/", async (CreateCommentRequest request, CommentDbContext db, ProfanityServiceClient profanityClient, CommentCache cache) =>
        {
            var comment = new Comment
            {
                ArticleId = request.ArticleId,
                Author = request.Author,
                Content = request.Content
            };

            var result = await profanityClient.CheckTextAsync(request.Content);

            if (result is null)
            {
                // Circuit open — save as Pending and accept the request optimistically
                comment.Status = CommentStatus.Pending;
                db.Comments.Add(comment);
                await db.SaveChangesAsync();
                return Results.Accepted($"/comments/{comment.Id}", new
                {
                    comment,
                    message = "Profanity service unavailable. Your comment is pending review."
                });
            }

            if (result.ContainsProfanity)
                return Results.BadRequest(new
                {
                    message = "Comment contains inappropriate language.",
                    matched = result.MatchedWords
                });

            comment.Status = CommentStatus.Approved;
            db.Comments.Add(comment);
            await db.SaveChangesAsync();

            // New approved comment — invalidate so the next GET reloads from DB
            await cache.InvalidateAsync(request.ArticleId);

            return Results.Created($"/comments/{comment.Id}", comment);
        });

        // GET /comments?articleId=... — cache-aside: Redis first, DB on miss
        group.MapGet("/", async ([FromQuery] Guid articleId, CommentDbContext db, CommentCache cache) =>
        {
            var cached = await cache.GetCommentsAsync(articleId);
            if (cached is not null)
                return Results.Ok(cached);

            // Cache miss — load from DB, populate cache for next time
            var comments = await db.Comments
                .Where(c => c.ArticleId == articleId && c.Status == CommentStatus.Approved)
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync();

            await cache.SetCommentsAsync(articleId, comments);
            return Results.Ok(comments);
        });

        // DELETE /comments/{id} — remove a comment; invalidate its article's cache entry
        group.MapDelete("/{id:guid}", async (Guid id, CommentDbContext db, CommentCache cache) =>
        {
            var comment = await db.Comments.FindAsync(id);
            if (comment is null) return Results.NotFound();

            var articleId = comment.ArticleId;
            db.Comments.Remove(comment);
            await db.SaveChangesAsync();

            await cache.InvalidateAsync(articleId);
            return Results.NoContent();
        });
    }
}

public record CreateCommentRequest(Guid ArticleId, string Author, string Content);
