using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;
using Xunit;

public sealed class MessagingTests(MongoFixture mongo)
    : IClassFixture<MongoFixture>
{
    [Fact]
    public async Task Consumer_creates_one_budget_and_outbox_worker_delivers_persistent_event()
    {
        await using var rabbit = new RabbitMqBuilder().WithImage("rabbitmq:3-management-alpine").WithUsername("test").WithPassword("test").Build();
        await rabbit.StartAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RabbitMq:Host"] = rabbit.Hostname,
            ["RabbitMq:Port"] = rabbit.GetMappedPublicPort(5672).ToString(),
            ["RabbitMq:User"] = "test",
            ["RabbitMq:Password"] = "test"
        }).Build();
        var database = new MongoClient(mongo.Container.GetConnectionString()).GetDatabase("messaging_test_" + Guid.NewGuid().ToString("N"));
        var budgets = database.GetCollection<Budget>("budgets");
        using var consumer = new OsCreatedConsumer(config, database, NullLogger<OsCreatedConsumer>.Instance);
        await consumer.StartAsync(CancellationToken.None);
        var settings = new ConnectionFactory { HostName = rabbit.Hostname, Port = rabbit.GetMappedPublicPort(5672), UserName = "test", Password = "test" };
        using var connection = settings.CreateConnection();
        using var channel = connection.CreateModel();
        var orderId = Guid.NewGuid();
        var body = JsonSerializer.Serialize(new { payload = new { orderId, description = "Transmission repair" } });
        channel.BasicPublish("oficina.events", "OS_CREATED", null, Encoding.UTF8.GetBytes(body));
        await Eventually(async () => await budgets.CountDocumentsAsync(x => x.OrderId == orderId) == 1);
        channel.BasicPublish("oficina.events", "OS_CREATED", null, Encoding.UTF8.GetBytes(body));
        await Task.Delay(300);
        Assert.Equal(1, await budgets.CountDocumentsAsync(x => x.OrderId == orderId));

        using var worker = new BillingOutboxWorker(database, new EventPublisher(config), NullLogger<BillingOutboxWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await Eventually(async () => (await budgets.Find(x => x.OrderId == orderId).SingleAsync()).PendingEvents.Count == 0);
            var message = channel.BasicGet("os-service-events", autoAck: true);
            Assert.NotNull(message);
            Assert.Equal("BUDGET_CREATED", message.RoutingKey);
            Assert.Equal(2, message.BasicProperties.DeliveryMode);
            using var envelope = JsonDocument.Parse(message.Body.ToArray());
            Assert.Equal(orderId, envelope.RootElement.GetProperty("payload").GetProperty("orderId").GetGuid());
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    private static async Task Eventually(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!await condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(100, timeout.Token);
        }
    }
}
