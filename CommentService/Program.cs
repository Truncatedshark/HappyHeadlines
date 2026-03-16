using CommentService.BackgroundServices;
using CommentService.Caching;
using CommentService.Clients;
using CommentService.Data;
using CommentService.Endpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Prometheus;

var builder = WebApplication.CreateBuilder(args);

// ── Database ──────────────────────────────────────────────────────────────────
var connectionString = Environment.GetEnvironmentVariable("DB_COMMENTS")
    ?? builder.Configuration.GetConnectionString("DB_COMMENTS")
    ?? throw new InvalidOperationException("Missing DB_COMMENTS connection string.");

builder.Services.AddDbContext<CommentDbContext>(options =>
    options.UseNpgsql(connectionString));

// ── ProfanityService HTTP client with circuit breaker ─────────────────────────
//
// Circuit breaker states:
//   CLOSED     — normal, all calls go through
//   OPEN       — ProfanityService is down; calls fail immediately (fast-fail)
//                Comments are saved as Pending during this window
//   HALF-OPEN  — one probe request is allowed through to test recovery
//
// Settings:
//   FailureRatio      — 50% of calls must fail to trip the breaker
//   MinimumThroughput — need at least 3 calls before the ratio is evaluated
//   SamplingDuration  — the failure ratio is measured over a 30s window
//   BreakDuration     — circuit stays open for 15s before going half-open

var profanityServiceUrl = Environment.GetEnvironmentVariable("PROFANITY_SERVICE_URL")
    ?? builder.Configuration["ProfanityServiceUrl"]
    ?? "http://profanityservice:8080";

builder.Services
    .AddHttpClient<ProfanityServiceClient>(client =>
    {
        client.BaseAddress = new Uri(profanityServiceUrl);
        client.Timeout = TimeSpan.FromSeconds(5);
    })
    .AddResilienceHandler("profanity-circuit-breaker", resilienceBuilder =>
    {
        resilienceBuilder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            FailureRatio = 0.5,
            MinimumThroughput = 3,
            SamplingDuration = TimeSpan.FromSeconds(30),
            BreakDuration = TimeSpan.FromSeconds(15),
            OnOpened = args =>
            {
                Console.WriteLine($"[CircuitBreaker] OPENED — ProfanityService is down. Comments will be saved as Pending.");
                return ValueTask.CompletedTask;
            },
            OnClosed = args =>
            {
                Console.WriteLine($"[CircuitBreaker] CLOSED — ProfanityService recovered.");
                return ValueTask.CompletedTask;
            },
            OnHalfOpened = args =>
            {
                Console.WriteLine($"[CircuitBreaker] HALF-OPEN — Sending probe request to ProfanityService.");
                return ValueTask.CompletedTask;
            }
        });
    });

// ── Cache ─────────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<CommentCache>();

// ── Background service to re-process Pending comments ─────────────────────────
builder.Services.AddHostedService<PendingCommentProcessor>();

var app = builder.Build();

// Ensure database exists
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CommentDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.MapCommentEndpoints();
app.MapMetrics();   // exposes /metrics for Prometheus scraping

app.Run();
