using Microsoft.EntityFrameworkCore;

public static class DraftEndpoints
{
    public static void MapDraftEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/drafts");

        group.MapPost("/", async (Draft draft, DraftDbContext db, ILogger<Draft> logger) =>
        {
            draft.Id = Guid.NewGuid();
            draft.CreatedAt = DateTime.UtcNow;
            draft.UpdatedAt = DateTime.UtcNow;
            db.Drafts.Add(draft);
            await db.SaveChangesAsync();
            logger.LogInformation("Draft {DraftId} created by {Author}", draft.Id, draft.Author);
            return Results.Created($"/drafts/{draft.Id}", draft);
        });

        group.MapGet("/", async (DraftDbContext db) =>
            await db.Drafts.ToListAsync());

        group.MapGet("/{id:guid}", async (Guid id, DraftDbContext db, ILogger<Draft> logger) =>
        {
            var draft = await db.Drafts.FindAsync(id);
            if (draft is null)
            {
                logger.LogWarning("Draft {DraftId} not found", id);
                return Results.NotFound();
            }
            return Results.Ok(draft);
        });

        group.MapPut("/{id:guid}", async (Guid id, Draft updated, DraftDbContext db, ILogger<Draft> logger) =>
        {
            var draft = await db.Drafts.FindAsync(id);
            if (draft is null)
            {
                logger.LogWarning("Draft {DraftId} not found for update", id);
                return Results.NotFound();
            }
            draft.Title = updated.Title;
            draft.Content = updated.Content;
            draft.Author = updated.Author;
            draft.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            logger.LogInformation("Draft {DraftId} updated by {Author}", id, draft.Author);
            return Results.Ok(draft);
        });

        group.MapDelete("/{id:guid}", async (Guid id, DraftDbContext db, ILogger<Draft> logger) =>
        {
            var draft = await db.Drafts.FindAsync(id);
            if (draft is null)
            {
                logger.LogWarning("Draft {DraftId} not found for deletion", id);
                return Results.NotFound();
            }
            db.Drafts.Remove(draft);
            await db.SaveChangesAsync();
            logger.LogInformation("Draft {DraftId} deleted", id);
            return Results.NoContent();
        });
    }
}
