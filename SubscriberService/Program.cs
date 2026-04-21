using FeatureHubSDK;
using Microsoft.EntityFrameworkCore;
using Prometheus;
using SubscriberService.Data;
using SubscriberService.Endpoints;
using SubscriberService.Messaging;

var builder = WebApplication.CreateBuilder(args);

// ── Database ──────────────────────────────────────────────────────────────────
var connectionString = Environment.GetEnvironmentVariable("DB_SUBSCRIBERS")
    ?? builder.Configuration.GetConnectionString("DB_SUBSCRIBERS")
    ?? throw new InvalidOperationException("Missing DB_SUBSCRIBERS connection string.");

builder.Services.AddDbContext<SubscriberDbContext>(options =>
    options.UseNpgsql(connectionString));

// ── RabbitMQ publisher ────────────────────────────────────────────────────────
builder.Services.AddSingleton<SubscriberQueuePublisher>();

// ── FeatureHub (release toggle) ───────────────────────────────────────────────
// Connects to FeatureHub via SSE — flag updates arrive in real-time without
// restarting or redeploying the service. Fail-open: if FeatureHub is unavailable
// or the API key is not yet configured, the service keeps running normally.
var featureHubEdgeUrl = Environment.GetEnvironmentVariable("FEATUREHUB_EDGE_URL") ?? "http://featurehub:8085/";
var featureHubApiKey  = Environment.GetEnvironmentVariable("FEATUREHUB_API_KEY")  ?? "";

IFeatureHubConfig? featureHubConfig = null;
if (!string.IsNullOrWhiteSpace(featureHubApiKey) && !featureHubApiKey.StartsWith("replace-"))
{
    featureHubConfig = new EdgeFeatureHubConfig(featureHubEdgeUrl, featureHubApiKey);
    featureHubConfig.Init();
    builder.Services.AddSingleton(featureHubConfig);
}
else
{
    Console.WriteLine("[FeatureHub] API key not configured — release toggle is inactive, service will run normally.");
}

var app = builder.Build();

// Ensure the subscribers table exists on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SubscriberDbContext>();
    await db.Database.EnsureCreatedAsync();
}

// ── Release toggle middleware ──────────────────────────────────────────────────
// Sits in front of every endpoint. When the "subscriber_service_enabled" flag is
// turned OFF in the FeatureHub UI, every request immediately gets a 503 — without
// touching code or redeploying. Turn it back ON in the UI and requests flow again.
if (featureHubConfig is not null)
{
    app.Use(async (context, next) =>
    {
        var fhContext = await featureHubConfig.NewContext().Build();
        var enabled   = fhContext["subscriber_service_enabled"].IsEnabled;

        if (!enabled)
        {
            context.Response.StatusCode  = StatusCodes.Status503ServiceUnavailable;
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("SubscriberService is currently disabled via feature flag.");
            return;
        }

        await next(context);
    });
}

app.MapSubscriberEndpoints();
app.MapMetrics(); // exposes /metrics for Prometheus

app.Run();
