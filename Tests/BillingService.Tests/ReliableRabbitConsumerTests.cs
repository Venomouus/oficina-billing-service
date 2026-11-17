using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;
using Xunit;

public sealed class ReliableRabbitConsumerTests
{
    [Fact]
    public async Task RetriesTransientFailure_AndDeadLettersPermanentFailure()
    {
        await using var rabbit = new RabbitMqBuilder().WithImage("rabbitmq:3-management-alpine")
            .WithUsername("test").WithPassword("test").Build();
        await rabbit.StartAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RabbitMq:Host"] = rabbit.Hostname,
            ["RabbitMq:Port"] = rabbit.GetMappedPublicPort(5672).ToString(),
            ["RabbitMq:User"] = "test",
            ["RabbitMq:Password"] = "test",
            ["RabbitMq:RetryDelayMs"] = "100"
        }).Build();
        using var connection = new ConnectionFactory
        {
            HostName = rabbit.Hostname, Port = rabbit.GetMappedPublicPort(5672),
            UserName = "test", Password = "test"
        }.CreateConnection();
        using var channel = connection.CreateModel();

        var queue = "saga-test-" + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var attempts = 0;
        var consumer = ReliableRabbitConsumer.RunAsync(config, queue, ["TEST_OK"], _ =>
        {
            if (Interlocked.Increment(ref attempts) < 3) throw new InvalidOperationException("temporario");
            return Task.CompletedTask;
        }, NullLogger.Instance, stop.Token);
        Assert.True(channel.MessageCount(queue) == 0);
        channel.BasicPublish("oficina.events", "TEST_OK", null, Encoding.UTF8.GetBytes("ok"));
        await UntilAsync(() => Volatile.Read(ref attempts) == 3);
        Assert.Null(channel.BasicGet(queue + ".dead", true));
        stop.Cancel();
        await consumer;

        var poisonQueue = "saga-test-" + Guid.NewGuid().ToString("N");
        using var stopPoison = new CancellationTokenSource();
        var failures = 0;
        var poisonConsumer = ReliableRabbitConsumer.RunAsync(config, poisonQueue, ["TEST_FAIL"], _ =>
        {
            Interlocked.Increment(ref failures);
            throw new InvalidOperationException("permanente");
        }, NullLogger.Instance, stopPoison.Token);
        Assert.True(channel.MessageCount(poisonQueue) == 0);
        channel.BasicPublish("oficina.events", "TEST_FAIL", null, Encoding.UTF8.GetBytes("poison"));
        await UntilAsync(() => channel.MessageCount(poisonQueue + ".dead") == 1);
        Assert.Equal(ReliableRabbitConsumer.MaxRetries + 1, Volatile.Read(ref failures));
        var dead = channel.BasicGet(poisonQueue + ".dead", true);
        Assert.NotNull(dead);
        Assert.Equal(ReliableRabbitConsumer.MaxRetries + 1, ReliableRabbitConsumer.RetryCount(dead.BasicProperties.Headers));
        stopPoison.Cancel();
        await poisonConsumer;

        var malformedQueue = "saga-test-" + Guid.NewGuid().ToString("N");
        using var stopMalformed = new CancellationTokenSource();
        var malformedAttempts = 0;
        var malformedConsumer = ReliableRabbitConsumer.RunAsync(config, malformedQueue, ["TEST_BAD"], _ =>
        {
            Interlocked.Increment(ref malformedAttempts);
            throw new System.Text.Json.JsonException("evento malformado");
        }, NullLogger.Instance, stopMalformed.Token);
        Assert.True(channel.MessageCount(malformedQueue) == 0);
        channel.BasicPublish("oficina.events", "TEST_BAD", null, Encoding.UTF8.GetBytes("bad"));
        await UntilAsync(() => channel.MessageCount(malformedQueue + ".dead") == 1);
        Assert.Equal(1, Volatile.Read(ref malformedAttempts));
        stopMalformed.Cancel();
        await malformedConsumer;
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) { timeout.Token.ThrowIfCancellationRequested(); await Task.Delay(50, timeout.Token); }
    }
}
