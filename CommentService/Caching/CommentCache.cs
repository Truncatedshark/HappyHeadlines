using System.Text.Json;
using CommentService.Models;
using Prometheus;
using StackExchange.Redis;

namespace CommentService.Caching;

public class CommentCache
{
    // Prometheus counters — queried by Grafana via Prometheus
    private static readonly Counter HitCounter = Metrics.CreateCounter(
        "comment_cache_hits_total", "Number of comment cache hits");
    private static readonly Counter MissCounter = Metrics.CreateCounter(
        "comment_cache_misses_total", "Number of comment cache misses");

    private readonly IDatabase _redis;
    private readonly ILogger<CommentCache> _logger;

    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(15);

    // LRU sorted set key — scores are Unix timestamps of last access
    private const string LruKey = "comments:lru";
    private const int LruCapacity = 30;   // keep at most 30 article comment lists in cache

    public CommentCache(IConfiguration config, ILogger<CommentCache> logger)
    {
        _logger = logger;
        var connection = ConnectionMultiplexer.Connect(
            config["REDIS_COMMENTS"] ?? "redis_comments:6379");
        _redis = connection.GetDatabase();
    }

    // ── Get ───────────────────────────────────────────────────────────────────
    public async Task<List<Comment>?> GetCommentsAsync(Guid articleId)
    {
        var value = await _redis.StringGetAsync(CacheKey(articleId));
        if (value.IsNullOrEmpty)
        {
            MissCounter.Inc();
            return null;
        }

        HitCounter.Inc();

        // Refresh the LRU score so this article's slot stays warm
        await _redis.SortedSetAddAsync(LruKey, articleId.ToString(), UnixNow());

        return JsonSerializer.Deserialize<List<Comment>>(value!);
    }

    // ── Set (cache-aside write + LRU maintenance) ─────────────────────────────
    public async Task SetCommentsAsync(Guid articleId, List<Comment> comments)
    {
        var key = CacheKey(articleId);

        // 1. Store the comment list
        await _redis.StringSetAsync(key, JsonSerializer.Serialize(comments), DefaultTtl);

        // 2. Record this articleId in the LRU sorted set (score = now)
        await _redis.SortedSetAddAsync(LruKey, articleId.ToString(), UnixNow());

        // 3. If we've exceeded capacity, evict the least-recently-used entries
        var count = await _redis.SortedSetLengthAsync(LruKey);
        if (count > LruCapacity)
        {
            var excess = count - LruCapacity;
            // ZRANGE with lowest scores = oldest accessed = LRU victims
            var victims = await _redis.SortedSetRangeByRankAsync(LruKey, 0, excess - 1);
            foreach (var victim in victims)
            {
                await _redis.KeyDeleteAsync(CacheKey(victim.ToString()));
                await _redis.SortedSetRemoveAsync(LruKey, victim);
                _logger.LogDebug("LRU evicted comments for article {ArticleId}", victim);
            }
        }

        _logger.LogDebug("Cached {Count} comments for article {ArticleId}", comments.Count, articleId);
    }

    // ── Invalidate (called on POST and DELETE) ─────────────────────────────────
    public async Task InvalidateAsync(Guid articleId)
    {
        await _redis.KeyDeleteAsync(CacheKey(articleId));
        await _redis.SortedSetRemoveAsync(LruKey, articleId.ToString());
    }

    private static string CacheKey(Guid articleId) => $"comments:{articleId}";
    private static string CacheKey(string articleId) => $"comments:{articleId}";
    private static double UnixNow() =>
        DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
