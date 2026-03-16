using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using RabbitMQ.Client;

public class ArticleQueuePublisher : IDisposable
{
    // A named ActivitySource lets OpenTelemetry know about spans we create manually.
    // It must be registered in Program.cs via .AddSource() to appear in Jaeger.
    private static readonly ActivitySource ActivitySource = new("PublisherService.Messaging");

    private readonly IConnection _connection;
    private readonly IModel _channel;
    private const string QueueName = "articles";

    public ArticleQueuePublisher(IConfiguration config)
    {
        var factory = new ConnectionFactory
        {
            HostName = config["RABBITMQ_HOST"] ?? "rabbitmq",
            UserName = config["RABBITMQ_USER"] ?? "guest",
            Password = config["RABBITMQ_PASS"] ?? "guest"
        };
        _connection = factory.CreateConnection();
        _channel = _connection.CreateModel();

        // Declare the queue once on startup. Durable = survives broker restart.
        _channel.QueueDeclare(QueueName, durable: true, exclusive: false, autoDelete: false);
    }

    public void Publish(ArticleMessage message)
    {
        // Start a Producer span — this appears in Jaeger as the publishing operation.
        using var activity = ActivitySource.StartActivity("articles publish", ActivityKind.Producer);

        var properties = _channel.CreateBasicProperties();
        properties.Headers = new Dictionary<string, object>();
        properties.ContentType = "application/json";
        properties.Persistent = true;

        // ── Trace context injection ────────────────────────────────────────────
        // This is what keeps the trace unbroken across the queue boundary.
        // Propagators.DefaultTextMapPropagator injects the W3C 'traceparent' header
        // into the message headers. The consumer reads this header to link its own
        // span back to this one, forming a single end-to-end trace in Jaeger.
        var propagationContext = new PropagationContext(activity?.Context ?? default, Baggage.Current);
        Propagators.DefaultTextMapPropagator.Inject(
            propagationContext,
            properties.Headers,
            (headers, key, value) => headers[key] = Encoding.UTF8.GetBytes(value));

        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        _channel.BasicPublish("", QueueName, properties, body);

        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination.name", QueueName);
        activity?.SetTag("messaging.operation.name", "publish");
        activity?.SetTag("article.id", message.Id.ToString());
        activity?.SetTag("article.region", message.Region);
    }

    public void Dispose()
    {
        _channel.Dispose();
        _connection.Dispose();
    }
}
