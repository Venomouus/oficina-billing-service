using System.Text;
using System.Diagnostics;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Oficina.Billing;

public static class ReliableRabbitConsumer
{
    public const int MaxRetries = 5;

    public static async Task RunAsync(
        IConfiguration configuration, string queue, string[] bindings,
        Func<ReadOnlyMemory<byte>, Task> process, ILogger logger, CancellationToken stoppingToken)
    {
        var settings = configuration.GetSection("RabbitMq");
        var factory = new ConnectionFactory
        {
            HostName = settings["Host"] ?? "localhost",
            Port = settings.GetValue("Port", 5672),
            UserName = settings["User"] ?? "oficina",
            Password = settings["Password"] ?? "oficina",
            DispatchConsumersAsync = true,
            AutomaticRecoveryEnabled = false
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var connection = factory.CreateConnection();
                using var channel = connection.CreateModel();
                channel.ExchangeDeclare("oficina.events", ExchangeType.Topic, durable: true);
                channel.ExchangeDeclare("oficina.events.retry", ExchangeType.Topic, durable: true);
                channel.QueueDeclare(queue, durable: true, exclusive: false, autoDelete: false);
                channel.QueueDeclare(queue + ".retry", durable: true, exclusive: false, autoDelete: false,
                    arguments: new Dictionary<string, object>
                    {
                        ["x-message-ttl"] = settings.GetValue("RetryDelayMs", 30000),
                        ["x-dead-letter-exchange"] = "oficina.events"
                    });
                channel.QueueDeclare(queue + ".dead", durable: true, exclusive: false, autoDelete: false);
                foreach (var binding in bindings)
                {
                    channel.QueueBind(queue, "oficina.events", binding);
                    channel.QueueBind(queue + ".retry", "oficina.events.retry", binding);
                }
                channel.BasicQos(0, 1, false);
                channel.ConfirmSelect();

                var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                connection.ConnectionShutdown += (_, _) => disconnected.TrySetResult();
                channel.ModelShutdown += (_, _) => disconnected.TrySetResult();
                using var registration = stoppingToken.Register(() => disconnected.TrySetResult());
                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.Received += async (_, delivery) =>
                {
                    using var activity = Observability.Activities.StartActivity("saga.consume", ActivityKind.Consumer);
                    activity?.SetTag("messaging.destination.name", queue);
                    activity?.SetTag("saga.event.type", delivery.RoutingKey);
                    try
                    {
                        await process(delivery.Body);
                        Observability.Consumed.Add(1, new KeyValuePair<string, object?>("event.type", delivery.RoutingKey));
                        channel.BasicAck(delivery.DeliveryTag, false);
                    }
                    catch (Exception exception)
                    {
                        Observability.Failed.Add(1, new KeyValuePair<string, object?>("event.type", delivery.RoutingKey));
                        activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
                        try
                        {
                            var malformed = exception is System.Text.Json.JsonException or KeyNotFoundException;
                            var retries = RetryCount(delivery.BasicProperties?.Headers);
                            var destination = malformed || retries >= MaxRetries ? queue + ".dead" : queue + ".retry";
                            var properties = channel.CreateBasicProperties();
                            properties.Persistent = true;
                            properties.Headers = new Dictionary<string, object>
                            {
                                ["x-saga-retries"] = retries + 1,
                                ["x-saga-error"] = exception.GetType().Name
                            };
                            var retry = destination.EndsWith(".retry", StringComparison.Ordinal);
                            channel.BasicPublish(retry ? "oficina.events.retry" : "",
                                retry ? delivery.RoutingKey : destination, properties, delivery.Body);
                            channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(5));
                            channel.BasicAck(delivery.DeliveryTag, false);
                            logger.LogWarning(exception, "Evento {RoutingKey} enviado para {Destination} (tentativa {Retry})",
                                delivery.RoutingKey, destination, retries + 1);
                        }
                        catch (Exception publishError)
                        {
                            logger.LogError(publishError, "Falha ao preservar evento {RoutingKey}; entrega sera repetida", delivery.RoutingKey);
                            if (channel.IsOpen) channel.BasicNack(delivery.DeliveryTag, false, true);
                        }
                    }
                };
                channel.BasicConsume(queue, false, consumer);
                await disconnected.Task;
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Consumidor {Queue} desconectado; tentando reconectar", queue);
            }
            if (!stoppingToken.IsCancellationRequested) await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    public static int RetryCount(IDictionary<string, object>? headers)
    {
        if (headers is null || !headers.TryGetValue("x-saga-retries", out var value)) return 0;
        return value switch
        {
            int count => count,
            long count => (int)count,
            byte[] bytes when int.TryParse(Encoding.UTF8.GetString(bytes), out var count) => count,
            _ => 0
        };
    }
}
