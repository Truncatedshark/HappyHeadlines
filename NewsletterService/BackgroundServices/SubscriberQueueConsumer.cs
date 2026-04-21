using System.Text;
using System.Text.Json;
using NewsletterService.Models;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace NewsletterService.BackgroundServices;

// Consumes from the "subscribers" queue.
// Every time SubscriberService adds a new subscriber, this consumer fires
// and sends a welcome mail — completely decoupled from SubscriberService at runtime.
public class SubscriberQueueConsumer : BackgroundService
{
    private readonly ILogger<SubscriberQueueConsumer> _logger;
    private readonly IConnection _connection;
    private readonly IModel _channel;
    private const string QueueName = "subscribers";

    public SubscriberQueueConsumer(ILogger<SubscriberQueueConsumer> logger, IConfiguration config)
    {
        _logger = logger;

        var factory = new ConnectionFactory
        {
            HostName = config["RABBITMQ_HOST"] ?? "rabbitmq",
            UserName = config["RABBITMQ_USER"] ?? "guest",
            Password = config["RABBITMQ_PASS"] ?? "guest"
        };

        _connection = factory.CreateConnection();
        _channel = _connection.CreateModel();

        // Declare idempotently — safe even if SubscriberService already declared this queue
        _channel.QueueDeclare(QueueName, durable: true, exclusive: false, autoDelete: false);
        _channel.BasicQos(prefetchSize: 0, prefetchCount: 1, global: false);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consumer = new EventingBasicConsumer(_channel);

        consumer.Received += (_, ea) =>
        {
            try
            {
                var body = Encoding.UTF8.GetString(ea.Body.ToArray());
                var subscriber = JsonSerializer.Deserialize<SubscriberMessage>(body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (subscriber is null)
                {
                    _logger.LogWarning("Received null or unparseable subscriber message — discarding");
                    _channel.BasicNack(ea.DeliveryTag, false, requeue: false);
                    return;
                }

                // In a real system this would call an SMTP service or mail provider.
                // For now we simulate sending by logging.
                _logger.LogInformation("Sending welcome mail to {Email} (subscriber {Id})",
                    subscriber.Email, subscriber.Id);

                _channel.BasicAck(ea.DeliveryTag, false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process subscriber message — requeuing");
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
