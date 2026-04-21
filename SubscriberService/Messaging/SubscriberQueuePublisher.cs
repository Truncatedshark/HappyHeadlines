using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using SubscriberService.Models;

namespace SubscriberService.Messaging;

public class SubscriberQueuePublisher : IDisposable
{
    private readonly IConnection _connection;
    private readonly IModel _channel;
    private const string QueueName = "subscribers";

    public SubscriberQueuePublisher(IConfiguration config)
    {
        var factory = new ConnectionFactory
        {
            HostName = config["RABBITMQ_HOST"] ?? "rabbitmq",
            UserName = config["RABBITMQ_USER"] ?? "guest",
            Password = config["RABBITMQ_PASS"] ?? "guest"
        };
        _connection = factory.CreateConnection();
        _channel = _connection.CreateModel();

        // Durable = queue survives a RabbitMQ restart, messages are not lost
        _channel.QueueDeclare(QueueName, durable: true, exclusive: false, autoDelete: false);
    }

    public void Publish(Subscriber subscriber)
    {
        var properties = _channel.CreateBasicProperties();
        properties.ContentType = "application/json";
        properties.Persistent = true;

        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(subscriber));
        _channel.BasicPublish("", QueueName, properties, body);
    }

    public void Dispose()
    {
        _channel.Dispose();
        _connection.Dispose();
    }
}
