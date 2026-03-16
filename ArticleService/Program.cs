using ArticleService.BackgroundServices;
using ArticleService.Caching;
using ArticleService.Data;
using ArticleService.Endpoints;
using ArticleService.Models;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Prometheus;
using Serilog;
using Serilog.Enrichers.Span;

var builder = WebApplication.CreateBuilder(args);

// ── Logging: Serilog → Console + Seq ─────────────────────────────────────
builder.Host.UseSerilog((context, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .WriteTo.Console()
    .WriteTo.Seq(context.Configuration["SEQ_URL"] ?? "http://seq")
    .Enrich.FromLogContext()
    .Enrich.WithSpan()
    .Enrich.WithProperty("Service", "ArticleService"));

// ── Tracing: OpenTelemetry → Jaeger ──────────────────────────────────────
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("ArticleService"))
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        // Register the consumer's ActivitySource so its spans appear in Jaeger
        .AddSource("ArticleService.Messaging")
        .AddOtlpExporter(options =>
        {
            options.Endpoint = new Uri(
                builder.Configuration["JAEGER_OTLP_ENDPOINT"] ?? "http://jaeger:4317");
        }));

builder.Services.AddSingleton<ArticleDbContextFactory>();
builder.Services.AddSingleton<ArticleCache>();
builder.Services.AddHostedService<ArticleQueueConsumer>();
builder.Services.AddHostedService<ArticleCacheWarmer>();

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
app.MapMetrics();   // exposes /metrics for Prometheus scraping

app.Run();
