using Microsoft.EntityFrameworkCore;
using ProfanityService.Data;
using ProfanityService.Endpoints;
using ProfanityService.Models;

var builder = WebApplication.CreateBuilder(args);

var connectionString = Environment.GetEnvironmentVariable("DB_PROFANITY")
    ?? builder.Configuration.GetConnectionString("DB_PROFANITY")
    ?? throw new InvalidOperationException("Missing DB_PROFANITY connection string.");

builder.Services.AddDbContext<ProfanityDbContext>(options =>
    options.UseNpgsql(connectionString));

var app = builder.Build();

// Ensure database exists and seed default words
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ProfanityDbContext>();
    await db.Database.EnsureCreatedAsync();

    if (!await db.ProfanityWords.AnyAsync())
    {
        db.ProfanityWords.AddRange(
            new ProfanityWord { Word = "badword" },
            new ProfanityWord { Word = "spam" },
            new ProfanityWord { Word = "hate" }
        );
        await db.SaveChangesAsync();
        app.Logger.LogInformation("Seeded default profanity words.");
    }
}

app.MapProfanityEndpoints();

app.Run();
