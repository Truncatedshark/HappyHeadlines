using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
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
    .Enrich.WithProperty("Service", "PublisherService"));

// ── Tracing: OpenTelemetry → Jaeger ──────────────────────────────────────
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("PublisherService"))
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        // Register our custom ActivitySource so Jaeger sees the publish span
        .AddSource("PublisherService.Messaging")
        .AddOtlpExporter(options =>
        {
            options.Endpoint = new Uri(
                builder.Configuration["JAEGER_OTLP_ENDPOINT"] ?? "http://jaeger:4317");
        }));

// ── Services ──────────────────────────────────────────────────────────────
// Singleton: one RabbitMQ connection shared for the lifetime of the app
builder.Services.AddSingleton<ArticleQueuePublisher>();

var app = builder.Build();

app.MapPublisherEndpoints();

app.Run();
