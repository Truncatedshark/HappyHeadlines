using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ArticleService.Data;
using ArticleService.Models;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ArticleService.BackgroundServices;

public class ArticleQueueConsumer : BackgroundService
{
    // Named ActivitySource so our consumer spans show up in Jaeger under ArticleService.
    private static readonly ActivitySource ActivitySource = new("ArticleService.Messaging");

    private readonly IServiceProvider _services;
    private readonly ILogger<ArticleQueueConsumer> _logger;
    private readonly IModel _channel;
    private readonly IConnection _connection;
    private const string QueueName = "articles";

    public ArticleQueueConsumer(
        IServiceProvider services,
        ILogger<ArticleQueueConsumer> logger,
        IConfiguration config)
    {
        _services = services;
        _logger = logger;

        var factory = new ConnectionFactory
        {
            HostName = config["RABBITMQ_HOST"] ?? "rabbitmq",
            UserName = config["RABBITMQ_USER"] ?? "guest",
            Password = config["RABBITMQ_PASS"] ?? "guest"
        };

        _connection = factory.CreateConnection();
        _channel = _connection.CreateModel();

        // Declare idempotently — safe to call even if PublisherService already declared it.
        _channel.QueueDeclare(QueueName, durable: true, exclusive: false, autoDelete: false);

        // Only one unacknowledged message at a time per consumer instance.
        // Ensures the 3 ArticleService instances share the queue load fairly.
        _channel.BasicQos(prefetchSize: 0, prefetchCount: 1, global: false);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consumer = new EventingBasicConsumer(_channel);

        consumer.Received += async (_, ea) =>
        {
            // ── Trace context extraction ───────────────────────────────────────
            // This is the other half of the trace handoff from PublisherService.
            // We read the W3C 'traceparent' header from the message and restore
            // the ActivityContext so our span becomes a child of the publisher's span.
            // Without this, Jaeger would show two disconnected traces instead of one.
            var parentContext = Propagators.DefaultTextMapPropagator.Extract(
                default,
                ea.BasicProperties.Headers,
                (headers, key) =>
                {
                    if (headers != null && headers.TryGetValue(key, out var value) && value is byte[] bytes)
                        return [Encoding.UTF8.GetString(bytes)];
                    return [];
                });

            Baggage.Current = parentContext.Baggage;

            using var activity = ActivitySource.StartActivity(
                "articles consume",
                ActivityKind.Consumer,
                parentContext.ActivityContext);

            try
            {
                var body = Encoding.UTF8.GetString(ea.Body.ToArray());
                var message = JsonSerializer.Deserialize<ArticleMessage>(body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (message is null)
                {
                    _logger.LogWarning("Received null or unparseable message from queue");
                    _channel.BasicNack(ea.DeliveryTag, false, false);
                    return;
                }

                if (!Enum.TryParse<Region>(message.Region, ignoreCase: true, out var region))
                {
                    _logger.LogWarning("Unknown region {Region} for article {ArticleId} — discarding", message.Region, message.Id);
                    _channel.BasicNack(ea.DeliveryTag, false, false);
                    return;
                }

                // Use a scoped service provider to get a fresh DbContext per message.
                using var scope = _services.CreateScope();
                var dbFactory = scope.ServiceProvider.GetRequiredService<ArticleDbContextFactory>();
                await using var db = dbFactory.CreateForRegion(region);

                db.Articles.Add(new Article
                {
                    Id = message.Id,
                    Title = message.Title,
                    Content = message.Content,
                    Author = message.Author,
                    Region = region,
                    PublishedAt = message.PublishedAt,
                    UpdatedAt = message.PublishedAt
                });

                await db.SaveChangesAsync();

                activity?.SetTag("messaging.system", "rabbitmq");
                activity?.SetTag("messaging.destination.name", QueueName);
                activity?.SetTag("messaging.operation.name", "consume");
                activity?.SetTag("article.id", message.Id.ToString());
                activity?.SetTag("article.region", region.ToString());

                _logger.LogInformation("Article {ArticleId} saved to {Region} database", message.Id, region);

                _channel.BasicAck(ea.DeliveryTag, false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process article message — requeuing");
                _channel.BasicNack(ea.DeliveryTag, false, requeue: true);
            }
        };

        _channel.BasicConsume(QueueName, autoAck: false, consumer: consumer);
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        _channel.Dispose();
        _connection.Dispose();
        base.Dispose();
    }
}
