using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(sp => { var section = sp.GetRequiredService<IConfiguration>().GetSection("Mongo"); return new MongoClient(section["ConnectionString"] ?? "mongodb://localhost:27017").GetDatabase(section["Database"] ?? "oficina_billing"); });
builder.Services.AddSingleton<EventPublisher>();
builder.Services.AddHostedService<OsCreatedConsumer>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
app.UseSwagger(); app.UseSwaggerUI();
app.MapGet("/health", () => Results.Ok(new { service = "billing-service", status = "UP" }));
app.MapGet("/api/budgets", async (IMongoDatabase db) => Results.Ok(await db.GetCollection<Budget>("budgets").Find(FilterDefinition<Budget>.Empty).SortByDescending(x => x.CreatedAt).ToListAsync()));
app.MapGet("/api/budgets/{id}", async (string id, IMongoDatabase db) => { var budget = await db.GetCollection<Budget>("budgets").Find(x => x.Id == id).FirstOrDefaultAsync(); return budget is null ? Results.NotFound() : Results.Ok(budget); });

app.MapPost("/api/budgets/{id}/approve", async (string id, IMongoDatabase db, EventPublisher events) =>
{
    var collection = db.GetCollection<Budget>("budgets");
    var budget = await collection.FindOneAndUpdateAsync(x => x.Id == id && x.Status == BudgetStatuses.PendingApproval, Builders<Budget>.Update.Set(x => x.Status, BudgetStatuses.Approved).Set(x => x.UpdatedAt, DateTimeOffset.UtcNow));
    if (budget is null) return Results.NotFound(new { error = "orcamento inexistente ou fora de aprovacao" });
    await events.PublishAsync("BUDGET_APPROVED", new { orderId = budget.OrderId, budgetId = budget.Id });
    return Results.Ok(budget);
});

app.MapPost("/api/budgets/{id}/reject", async (string id, RejectBudgetRequest request, IMongoDatabase db, EventPublisher events) =>
{
    var collection = db.GetCollection<Budget>("budgets");
    var budget = await collection.FindOneAndUpdateAsync(x => x.Id == id && x.Status == BudgetStatuses.PendingApproval, Builders<Budget>.Update.Set(x => x.Status, BudgetStatuses.Rejected).Set(x => x.RejectionReason, request.Reason).Set(x => x.UpdatedAt, DateTimeOffset.UtcNow));
    if (budget is null) return Results.NotFound();
    await events.PublishAsync("BUDGET_REJECTED", new { orderId = budget.OrderId, budgetId = budget.Id, reason = request.Reason });
    return Results.Ok(budget);
});

app.MapPost("/api/budgets/{id}/payment", async (string id, IMongoDatabase db, EventPublisher events) =>
{
    var collection = db.GetCollection<Budget>("budgets");
    var budget = await collection.FindOneAndUpdateAsync(x => x.Id == id && x.Status == BudgetStatuses.Approved, Builders<Budget>.Update.Set(x => x.Status, BudgetStatuses.Paid).Set(x => x.PaymentProvider, "mercado-pago-sandbox").Set(x => x.UpdatedAt, DateTimeOffset.UtcNow));
    if (budget is null) return Results.NotFound(new { error = "orcamento inexistente ou nao aprovado" });
    await events.PublishAsync("PAYMENT_APPROVED", new { orderId = budget.OrderId, budgetId = budget.Id, provider = budget.PaymentProvider });
    return Results.Ok(budget);
});

app.Run();

public record RejectBudgetRequest(string Reason);
public static class BudgetStatuses { public const string PendingApproval = "PENDING_APPROVAL"; public const string Approved = "APPROVED"; public const string Rejected = "REJECTED"; public const string Paid = "PAID"; }
public class Budget
{
    [BsonId] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public Guid OrderId { get; set; }
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
    public string Status { get; set; } = BudgetStatuses.PendingApproval;
    public string? RejectionReason { get; set; }
    public string? PaymentProvider { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class EventPublisher(IConfiguration configuration)
{
    public Task PublishAsync(string type, object payload)
    {
        var settings = configuration.GetSection("RabbitMq"); var factory = new ConnectionFactory { HostName = settings["Host"] ?? "localhost", Port = settings.GetValue("Port", 5672), UserName = settings["User"] ?? "oficina", Password = settings["Password"] ?? "oficina" }; using var connection = factory.CreateConnection(); using var channel = connection.CreateModel(); channel.ExchangeDeclare("oficina.events", ExchangeType.Topic, durable: true); channel.BasicPublish("oficina.events", type, null, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type, payload, occurredAt = DateTimeOffset.UtcNow }))); return Task.CompletedTask;
    }
}

public sealed class OsCreatedConsumer(IConfiguration configuration, IMongoDatabase db, EventPublisher events, ILogger<OsCreatedConsumer> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = configuration.GetSection("RabbitMq"); var factory = new ConnectionFactory { HostName = settings["Host"] ?? "localhost", Port = settings.GetValue("Port", 5672), UserName = settings["User"] ?? "oficina", Password = settings["Password"] ?? "oficina" }; var connection = factory.CreateConnection(); var channel = connection.CreateModel(); channel.ExchangeDeclare("oficina.events", ExchangeType.Topic, durable: true); var queue = channel.QueueDeclare("billing-service-events", true, false, false).QueueName; channel.QueueBind(queue, "oficina.events", "OS_CREATED");
        var consumer = new EventingBasicConsumer(channel);
        consumer.Received += async (_, args) =>
        {
            try
            {
                using var document = JsonDocument.Parse(args.Body.ToArray()); var payload = document.RootElement.GetProperty("payload"); var orderId = payload.GetProperty("orderId").GetGuid(); var description = payload.GetProperty("description").GetString() ?? "Servico mecanico"; var collection = db.GetCollection<Budget>("budgets"); var existing = await collection.Find(x => x.OrderId == orderId).FirstOrDefaultAsync();
                if (existing is null)
                {
                    var budget = new Budget { OrderId = orderId, Description = description, Amount = 0 }; await collection.InsertOneAsync(budget); await events.PublishAsync("BUDGET_CREATED", new { orderId, budgetId = budget.Id, description });
                }
                channel.BasicAck(args.DeliveryTag, false);
            }
            catch (Exception ex) { logger.LogError(ex, "Falha ao gerar orcamento"); channel.BasicNack(args.DeliveryTag, false, true); }
        };
        channel.BasicConsume(queue, false, consumer); return Task.CompletedTask;
    }
}
