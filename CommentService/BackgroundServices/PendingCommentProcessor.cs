using CommentService.Clients;
using CommentService.Data;
using CommentService.Models;
using Microsoft.EntityFrameworkCore;

namespace CommentService.BackgroundServices;

// Runs every 30 seconds and retries profanity checks for Pending comments.
// When the circuit breaker closes (ProfanityService recovers), pending
// comments are approved or rejected rather than staying in limbo.
public class PendingCommentProcessor : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PendingCommentProcessor> _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    public PendingCommentProcessor(IServiceScopeFactory scopeFactory, ILogger<PendingCommentProcessor> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(Interval, stoppingToken);
            await ProcessPendingCommentsAsync(stoppingToken);
        }
    }

    private async Task ProcessPendingCommentsAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommentDbContext>();
        var client = scope.ServiceProvider.GetRequiredService<ProfanityServiceClient>();

        var pending = await db.Comments
            .Where(c => c.Status == CommentStatus.Pending)
            .ToListAsync(stoppingToken);

        if (pending.Count == 0) return;

        _logger.LogInformation("Retrying profanity check for {Count} pending comment(s).", pending.Count);

        foreach (var comment in pending)
        {
            var result = await client.CheckTextAsync(comment.Content);

            if (result is null)
            {
                // Circuit still open — stop processing this batch and wait for next cycle
                _logger.LogWarning("ProfanityService still unavailable. Will retry in {Seconds}s.", Interval.TotalSeconds);
                break;
            }

            comment.Status = result.ContainsProfanity ? CommentStatus.Rejected : CommentStatus.Approved;
            _logger.LogInformation("Comment {Id} resolved to {Status}.", comment.Id, comment.Status);
        }

        await db.SaveChangesAsync(stoppingToken);
    }
}
