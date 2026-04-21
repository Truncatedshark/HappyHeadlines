using Microsoft.Extensions.Http.Resilience;
using NewsletterService.BackgroundServices;
using NewsletterService.Clients;
using NewsletterService.Endpoints;
using Polly;
using Prometheus;

var builder = WebApplication.CreateBuilder(args);

// ── SubscriberService HTTP client with circuit breaker ─────────────────────────
//
// Fault isolation: if SubscriberService goes down, the circuit opens and calls
// fail immediately (fast-fail) instead of piling up and taking down this service too.
//
// Circuit breaker states:
//   CLOSED    — normal, requests go through to SubscriberService
//   OPEN      — SubscriberService is down; calls fail immediately
//   HALF-OPEN — one probe request is sent to test if SubscriberService recovered

var subscriberServiceUrl = Environment.GetEnvironmentVariable("SUBSCRIBER_SERVICE_URL")
    ?? builder.Configuration["SubscriberServiceUrl"]
    ?? "http://subscriberservice:8080";

builder.Services
    .AddHttpClient<SubscriberServiceClient>(client =>
    {
        client.BaseAddress = new Uri(subscriberServiceUrl);
        client.Timeout = TimeSpan.FromSeconds(5);
    })
    .AddResilienceHandler("subscriber-circuit-breaker", resilienceBuilder =>
    {
        resilienceBuilder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            FailureRatio = 0.5,
            MinimumThroughput = 3,
            SamplingDuration = TimeSpan.FromSeconds(30),
            BreakDuration = TimeSpan.FromSeconds(15),
            OnOpened = args =>
            {
                Console.WriteLine("[CircuitBreaker] OPENED — SubscriberService is down. Newsletter sends will return 503.");
                return ValueTask.CompletedTask;
            },
            OnClosed = args =>
            {
                Console.WriteLine("[CircuitBreaker] CLOSED — SubscriberService recovered.");
                return ValueTask.CompletedTask;
            },
            OnHalfOpened = args =>
            {
                Console.WriteLine("[CircuitBreaker] HALF-OPEN — Sending probe request to SubscriberService.");
                return ValueTask.CompletedTask;
            }
        });
    });

// ── Queue consumer (welcome mails) ────────────────────────────────────────────
// Runs as a background service — listens to the "subscribers" queue independently
// of whether SubscriberService HTTP is reachable or not
builder.Services.AddHostedService<SubscriberQueueConsumer>();

var app = builder.Build();

app.MapNewsletterEndpoints();
app.MapMetrics(); // exposes /metrics for Prometheus

app.Run();
