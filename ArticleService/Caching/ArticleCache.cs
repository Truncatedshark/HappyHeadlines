using System.Text.Json;
using ArticleService.Models;
using Prometheus;
using StackExchange.Redis;

namespace ArticleService.Caching;

public class ArticleCache
{
    // Prometheus counters — these are what Grafana queries via Prometheus
    private static readonly Counter HitCounter = Metrics.CreateCounter(
        "article_cache_hits_total", "Number of article cache hits");
    private static readonly Counter MissCounter = Metrics.CreateCounter(
        "article_cache_misses_total", "Number of article cache misses");

    private readonly IDatabase _redis;
    private readonly ILogger<ArticleCache> _logger;

    // TTL for cached entries — warmer refreshes every 5 minutes so 15 min is a safe buffer
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(15);

    public ArticleCache(IConfiguration config, ILogger<ArticleCache> logger)
    {
        _logger = logger;
        var connection = ConnectionMultiplexer.Connect(
            config["REDIS_ARTICLES"] ?? "redis_articles:6379");
        _redis = connection.GetDatabase();
    }

    // ── List (all articles for a region) ──────────────────────────────────
    public async Task<List<Article>?> GetArticlesAsync(Region region)
    {
        var value = await _redis.StringGetAsync(ListKey(region));
        if (value.IsNullOrEmpty)
        {
            MissCounter.Inc();
            return null;
        }
        HitCounter.Inc();
        return JsonSerializer.Deserialize<List<Article>>(value!);
    }

    public async Task SetArticlesAsync(Region region, List<Article> articles, TimeSpan? ttl = null)
    {
        await _redis.StringSetAsync(
            ListKey(region),
            JsonSerializer.Serialize(articles),
            ttl ?? DefaultTtl);
        _logger.LogDebug("Cached {Count} articles for region {Region}", articles.Count, region);
    }

    // ── Single article ─────────────────────────────────────────────────────
    public async Task<Article?> GetArticleAsync(Guid id, Region region)
    {
        var value = await _redis.StringGetAsync(ItemKey(id, region));
        if (value.IsNullOrEmpty)
        {
            MissCounter.Inc();
            return null;
        }
        HitCounter.Inc();
        return JsonSerializer.Deserialize<Article>(value!);
    }

    public async Task SetArticleAsync(Article article, TimeSpan? ttl = null)
    {
        await _redis.StringSetAsync(
            ItemKey(article.Id, article.Region),
            JsonSerializer.Serialize(article),
            ttl ?? DefaultTtl);
    }

    private static string ListKey(Region region) => $"articles:{region}";
    private static string ItemKey(Guid id, Region region) => $"article:{region}:{id}";
}
