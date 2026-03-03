using Microsoft.EntityFrameworkCore;
using ProfanityService.Data;
using ProfanityService.Models;

namespace ProfanityService.Endpoints;

public static class ProfanityEndpoints
{
    public static void MapProfanityEndpoints(this WebApplication app)
    {
        // POST /check — called by CommentService to screen text
        app.MapPost("/check", async (CheckRequest request, ProfanityDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(request.Text))
                return Results.BadRequest("Text cannot be empty.");

            var words = await db.ProfanityWords.Select(w => w.Word).ToListAsync();
            var lower = request.Text.ToLowerInvariant();
            var matched = words.Where(w => lower.Contains(w.ToLowerInvariant())).ToList();

            return Results.Ok(new CheckResponse(matched.Count > 0, matched));
        });

        // GET /words — list all profanity words
        app.MapGet("/words", async (ProfanityDbContext db) =>
            Results.Ok(await db.ProfanityWords.ToListAsync()));

        // POST /words — add a new word
        app.MapPost("/words", async (AddWordRequest request, ProfanityDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(request.Word))
                return Results.BadRequest("Word cannot be empty.");

            var exists = await db.ProfanityWords
                .AnyAsync(w => w.Word.ToLower() == request.Word.ToLower());
            if (exists)
                return Results.Conflict("Word already exists.");

            var entry = new ProfanityWord { Word = request.Word.ToLowerInvariant() };
            db.ProfanityWords.Add(entry);
            await db.SaveChangesAsync();
            return Results.Created($"/words/{entry.Id}", entry);
        });

        // DELETE /words/{word} — remove a word
        app.MapDelete("/words/{word}", async (string word, ProfanityDbContext db) =>
        {
            var entry = await db.ProfanityWords
                .FirstOrDefaultAsync(w => w.Word.ToLower() == word.ToLower());
            if (entry is null) return Results.NotFound();

            db.ProfanityWords.Remove(entry);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}

public record CheckRequest(string Text);
public record CheckResponse(bool ContainsProfanity, List<string> MatchedWords);
public record AddWordRequest(string Word);
