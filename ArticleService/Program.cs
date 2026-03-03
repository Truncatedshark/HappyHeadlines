using ArticleService.Data;
using ArticleService.Endpoints;
using ArticleService.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ArticleDbContextFactory>();

var app = builder.Build();

// Ensure all 8 shard databases exist on startup
var factory = app.Services.GetRequiredService<ArticleDbContextFactory>();
foreach (var region in Enum.GetValues<Region>())
{
    try
    {
        await using var db = factory.CreateForRegion(region);
        await db.Database.EnsureCreatedAsync();
        app.Logger.LogInformation("Database ready for region: {Region}", region);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning("Could not initialise {Region} database: {Message}", region, ex.Message);
    }
}

app.MapArticleEndpoints();

app.Run();
