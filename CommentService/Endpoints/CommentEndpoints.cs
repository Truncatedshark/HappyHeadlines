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
        group.MapPost("/", async (CreateCommentRequest request, CommentDbContext db, ProfanityServiceClient profanityClient) =>
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
            return Results.Created($"/comments/{comment.Id}", comment);
        });

        // GET /comments?articleId=... — fetch approved comments for an article
        group.MapGet("/", async ([FromQuery] Guid articleId, CommentDbContext db) =>
        {
            var comments = await db.Comments
                .Where(c => c.ArticleId == articleId && c.Status == CommentStatus.Approved)
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync();
            return Results.Ok(comments);
        });

        // DELETE /comments/{id} — remove a comment
        group.MapDelete("/{id:guid}", async (Guid id, CommentDbContext db) =>
        {
            var comment = await db.Comments.FindAsync(id);
            if (comment is null) return Results.NotFound();

            db.Comments.Remove(comment);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}

public record CreateCommentRequest(Guid ArticleId, string Author, string Content);
