using ArticleService.Caching;
using ArticleService.Data;
using ArticleService.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ArticleService.Endpoints;

public static class ArticleEndpoints
{
    public static void MapArticleEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/articles");

        // POST /articles — writes go directly to DB and also update the item cache
        group.MapPost("/", async (CreateArticleRequest request, ArticleDbContextFactory factory, ArticleCache cache) =>
        {
            if (!Enum.TryParse<Region>(request.Region, ignoreCase: true, out var region))
                return Results.BadRequest($"Invalid region '{request.Region}'. Valid values: {string.Join(", ", Enum.GetNames<Region>())}");

            var article = new Article
            {
                Title = request.Title,
                Content = request.Content,
                Author = request.Author,
                Region = region
            };

            await using var db = factory.CreateForRegion(region);
            db.Articles.Add(article);
            await db.SaveChangesAsync();

            // Cache the new article so it's immediately available for single-item reads
            await cache.SetArticleAsync(article);

            return Results.Created($"/articles/{article.Id}?region={region}", article);
        });

        // GET /articles?region=Europe — cache-aside: Redis first, DB on miss
        group.MapGet("/", async ([FromQuery] string region, ArticleDbContextFactory factory, ArticleCache cache) =>
        {
            if (!Enum.TryParse<Region>(region, ignoreCase: true, out var parsedRegion))
                return Results.BadRequest($"Invalid region '{region}'. Valid values: {string.Join(", ", Enum.GetNames<Region>())}");

            var cached = await cache.GetArticlesAsync(parsedRegion);
            if (cached is not null)
                return Results.Ok(cached);

            // Cache miss — load from DB and populate cache for next time
            await using var db = factory.CreateForRegion(parsedRegion);
            var articles = await db.Articles.ToListAsync();
            await cache.SetArticlesAsync(parsedRegion, articles);
            return Results.Ok(articles);
        });

        // GET /articles/{id}?region=Europe — cache-aside on single item
        group.MapGet("/{id:guid}", async (Guid id, [FromQuery] string region, ArticleDbContextFactory factory, ArticleCache cache) =>
        {
            if (!Enum.TryParse<Region>(region, ignoreCase: true, out var parsedRegion))
                return Results.BadRequest($"Invalid region '{region}'. Valid values: {string.Join(", ", Enum.GetNames<Region>())}");

            var cached = await cache.GetArticleAsync(id, parsedRegion);
            if (cached is not null)
                return Results.Ok(cached);

            // Cache miss — load from DB and populate cache
            await using var db = factory.CreateForRegion(parsedRegion);
            var article = await db.Articles.FindAsync(id);
            if (article is null) return Results.NotFound();

            await cache.SetArticleAsync(article);
            return Results.Ok(article);
        });

        // PUT /articles/{id}?region=Europe — writes go to DB; invalidate cached entries
        group.MapPut("/{id:guid}", async (Guid id, [FromQuery] string region, UpdateArticleRequest request, ArticleDbContextFactory factory, ArticleCache cache) =>
        {
            if (!Enum.TryParse<Region>(region, ignoreCase: true, out var parsedRegion))
                return Results.BadRequest($"Invalid region '{region}'. Valid values: {string.Join(", ", Enum.GetNames<Region>())}");

            await using var db = factory.CreateForRegion(parsedRegion);
            var article = await db.Articles.FindAsync(id);
            if (article is null) return Results.NotFound();

            article.Title = request.Title;
            article.Content = request.Content;
            article.Author = request.Author;
            article.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();

            // Re-cache the updated article so stale data isn't served
            await cache.SetArticleAsync(article);

            return Results.Ok(article);
        });

        // DELETE /articles/{id}?region=Europe
        group.MapDelete("/{id:guid}", async (Guid id, [FromQuery] string region, ArticleDbContextFactory factory) =>
        {
            if (!Enum.TryParse<Region>(region, ignoreCase: true, out var parsedRegion))
                return Results.BadRequest($"Invalid region '{region}'. Valid values: {string.Join(", ", Enum.GetNames<Region>())}");

            await using var db = factory.CreateForRegion(parsedRegion);
            var article = await db.Articles.FindAsync(id);
            if (article is null) return Results.NotFound();

            db.Articles.Remove(article);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}

public record CreateArticleRequest(string Title, string Content, string Author, string Region);
public record UpdateArticleRequest(string Title, string Content, string Author);
