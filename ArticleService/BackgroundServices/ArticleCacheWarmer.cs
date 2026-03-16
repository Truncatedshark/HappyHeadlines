using ArticleService.Caching;
using ArticleService.Data;
using ArticleService.Models;
using Microsoft.EntityFrameworkCore;

namespace ArticleService.BackgroundServices;

public class ArticleCacheWarmer : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ArticleCache _cache;
    private readonly ILogger<ArticleCacheWarmer> _logger;

    // Warm on startup, then every 5 minutes
    private static readonly TimeSpan WarmInterval = TimeSpan.FromMinutes(5);
    // Cache TTL is slightly longer than the warm interval so entries never expire between runs
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    public ArticleCacheWarmer(
        IServiceProvider services,
        ArticleCache cache,
        ILogger<ArticleCacheWarmer> logger)
    {
        _services = services;
        _cache = cache;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Warm immediately on startup so the cache is hot before the first request arrives,
        // then repeat on the interval.
        while (!stoppingToken.IsCancellationRequested)
        {
            await WarmAllRegionsAsync();
            await Task.Delay(WarmInterval, stoppingToken);
        }
    }

    private async Task WarmAllRegionsAsync()
    {
        var cutoff = DateTime.UtcNow.AddDays(-14);
        _logger.LogInformation("Cache warmer starting — loading articles since {Cutoff:u}", cutoff);

        using var scope = _services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<ArticleDbContextFactory>();

        foreach (var region in Enum.GetValues<Region>())
        {
            try
            {
                await using var db = factory.CreateForRegion(region);
                var articles = await db.Articles
                    .Where(a => a.PublishedAt >= cutoff)
                    .ToListAsync();

                await _cache.SetArticlesAsync(region, articles, CacheTtl);
                _logger.LogInformation(
                    "Cache warmed for {Region}: {Count} articles", region, articles.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cache warm failed for region {Region}", region);
            }
        }
    }
}
