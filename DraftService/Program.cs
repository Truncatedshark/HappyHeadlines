using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Enrichers.Span;

var builder = WebApplication.CreateBuilder(args);

// ── Logging: Serilog → Console + Seq ─────────────────────────────────────
// Serilog replaces the default .NET logger. Every ILogger<T> call in the app
// is routed through Serilog, which writes to the console and ships structured
// JSON events to Seq. WithSpan() attaches the current OpenTelemetry TraceId
// and SpanId to each log entry so logs can be linked to traces in Jaeger.
builder.Host.UseSerilog((context, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .WriteTo.Console()
    .WriteTo.Seq(context.Configuration["SEQ_URL"] ?? "http://seq")
    .Enrich.FromLogContext()
    .Enrich.WithSpan()
    .Enrich.WithProperty("Service", "DraftService"));

// ── Tracing: OpenTelemetry → Jaeger ──────────────────────────────────────
// OpenTelemetry automatically creates a trace span for every incoming HTTP
// request and every outgoing HTTP call. Spans are exported via OTLP to
// Jaeger where they can be visualised as a timeline.
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("DraftService"))
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(options =>
        {
            options.Endpoint = new Uri(
                builder.Configuration["JAEGER_OTLP_ENDPOINT"] ?? "http://jaeger:4317");
        }));

// ── Database ──────────────────────────────────────────────────────────────
builder.Services.AddDbContext<DraftDbContext>(options =>
    options.UseNpgsql(builder.Configuration["DB_DRAFTS"]));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DraftDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.MapDraftEndpoints();

app.Run();
