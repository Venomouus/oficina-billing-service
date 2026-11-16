using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Oficina.Billing;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<HostOptions>(options => options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost);
builder.Services.AddSingleton(sp => { var section = sp.GetRequiredService<IConfiguration>().GetSection("Mongo"); return new MongoClient(section["ConnectionString"] ?? "mongodb://localhost:27017").GetDatabase(section["Database"] ?? "oficina_billing"); });
builder.Services.AddSingleton<EventPublisher>();
builder.Services.AddHttpClient<MercadoPagoClient>();
builder.Services.AddHostedService<OsCreatedConsumer>();
builder.Services.AddHostedService<BillingOutboxWorker>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
app.UseSwagger(); app.UseSwaggerUI();
app.MapGet("/health", () => Results.Ok(new { service = "billing-service", status = "UP" }));
app.MapGet("/api/budgets", async (IMongoDatabase db) => Results.Ok(await db.GetCollection<Budget>("budgets").Find(FilterDefinition<Budget>.Empty).SortByDescending(x => x.CreatedAt).ToListAsync()));
app.MapGet("/api/budgets/{id}", async (string id, IMongoDatabase db) => { var budget = await db.GetCollection<Budget>("budgets").Find(x => x.Id == id).FirstOrDefaultAsync(); return budget is null ? Results.NotFound() : Results.Ok(budget); });

app.MapPost("/api/budgets/{id}/approve", async (string id, IMongoDatabase db) =>
{
    var collection = db.GetCollection<Budget>("budgets");
    var current = await collection.Find(x => x.Id == id && x.Status == BudgetStatuses.PendingApproval).FirstOrDefaultAsync();
    if (current is null) return Results.NotFound(new { error = "orcamento inexistente ou fora de aprovacao" });
    var pending = SagaOutboxEvent.Create("BUDGET_APPROVED", new { orderId = current.OrderId, budgetId = id });
    var budget = await collection.FindOneAndUpdateAsync<Budget>(x => x.Id == id && x.Status == BudgetStatuses.PendingApproval, Builders<Budget>.Update.Set(x => x.Status, BudgetStatuses.Approved).Set(x => x.UpdatedAt, DateTimeOffset.UtcNow).Push(x => x.PendingEvents, pending), new FindOneAndUpdateOptions<Budget> { ReturnDocument = ReturnDocument.After });
    if (budget is null) return Results.NotFound(new { error = "orcamento inexistente ou fora de aprovacao" });
    return Results.Ok(budget);
});

app.MapPatch("/api/budgets/{id}/amount", async (string id, UpdateBudgetAmountRequest request, IMongoDatabase db) =>
{
    if (request.Amount <= 0) return Results.BadRequest(new { error = "amount deve ser maior que zero" });
    var budget = await db.GetCollection<Budget>("budgets").FindOneAndUpdateAsync<Budget>(x => x.Id == id && x.Status == BudgetStatuses.PendingApproval,
        Builders<Budget>.Update.Set(x => x.Amount, request.Amount).Set(x => x.UpdatedAt, DateTimeOffset.UtcNow),
        new FindOneAndUpdateOptions<Budget> { ReturnDocument = ReturnDocument.After });
    return budget is null ? Results.NotFound() : Results.Ok(budget);
});

app.MapPost("/api/budgets/{id}/reject", async (string id, RejectBudgetRequest request, IMongoDatabase db) =>
{
    var collection = db.GetCollection<Budget>("budgets");
    var current = await collection.Find(x => x.Id == id && x.Status == BudgetStatuses.PendingApproval).FirstOrDefaultAsync();
    if (current is null) return Results.NotFound();
    var pending = SagaOutboxEvent.Create("BUDGET_REJECTED", new { orderId = current.OrderId, budgetId = id, reason = request.Reason });
    var budget = await collection.FindOneAndUpdateAsync<Budget>(x => x.Id == id && x.Status == BudgetStatuses.PendingApproval, Builders<Budget>.Update.Set(x => x.Status, BudgetStatuses.Rejected).Set(x => x.RejectionReason, request.Reason).Set(x => x.UpdatedAt, DateTimeOffset.UtcNow).Push(x => x.PendingEvents, pending), new FindOneAndUpdateOptions<Budget> { ReturnDocument = ReturnDocument.After });
    if (budget is null) return Results.NotFound();
    return Results.Ok(budget);
});

app.MapPost("/api/budgets/{id}/payment", async (string id, IMongoDatabase db) =>
{
    var collection = db.GetCollection<Budget>("budgets");
    var current = await collection.Find(x => x.Id == id && x.Status == BudgetStatuses.Approved).FirstOrDefaultAsync();
    if (current is null) return Results.NotFound(new { error = "orcamento inexistente ou nao aprovado" });
    var pending = SagaOutboxEvent.Create("PAYMENT_APPROVED", new { orderId = current.OrderId, budgetId = id, provider = "mercado-pago-sandbox" });
    var budget = await collection.FindOneAndUpdateAsync<Budget>(x => x.Id == id && x.Status == BudgetStatuses.Approved, Builders<Budget>.Update.Set(x => x.Status, BudgetStatuses.Paid).Set(x => x.PaymentProvider, "mercado-pago-sandbox").Set(x => x.UpdatedAt, DateTimeOffset.UtcNow).Push(x => x.PendingEvents, pending), new FindOneAndUpdateOptions<Budget> { ReturnDocument = ReturnDocument.After });
    if (budget is null) return Results.NotFound(new { error = "orcamento inexistente ou nao aprovado" });
    return Results.Ok(budget);
});

app.MapPost("/api/budgets/{id}/payment/preference", async (string id, IMongoDatabase db, MercadoPagoClient mercadoPago) =>
{
    var budget = await db.GetCollection<Budget>("budgets").Find(x => x.Id == id && x.Status == BudgetStatuses.Approved).FirstOrDefaultAsync();
    if (budget is null) return Results.NotFound(new { error = "orcamento inexistente ou nao aprovado" });
    if (string.IsNullOrWhiteSpace(mercadoPago.AccessToken)) return Results.Problem("Mercado Pago nao configurado. Defina MercadoPago__AccessToken.", statusCode: 503);

    var preference = await mercadoPago.CreatePreferenceAsync(budget);
    var updated = await db.GetCollection<Budget>("budgets").FindOneAndUpdateAsync<Budget, Budget>(
        x => x.Id == id,
        Builders<Budget>.Update.Set(x => x.MercadoPagoPreferenceId, preference.Id).Set(x => x.UpdatedAt, DateTimeOffset.UtcNow),
        new FindOneAndUpdateOptions<Budget> { ReturnDocument = ReturnDocument.After });
    return Results.Ok(new { budget = updated, preferenceId = preference.Id, checkoutUrl = preference.SandboxInitPoint ?? preference.InitPoint });
});

app.MapPost("/api/budgets/{id}/payment/qr", async (string id, IMongoDatabase db, MercadoPagoClient mercadoPago) =>
{
    var budget = await db.GetCollection<Budget>("budgets").Find(x => x.Id == id && x.Status == BudgetStatuses.Approved).FirstOrDefaultAsync();
    if (budget is null) return Results.NotFound(new { error = "orcamento inexistente ou nao aprovado" });
    if (string.IsNullOrWhiteSpace(mercadoPago.AccessToken) || string.IsNullOrWhiteSpace(mercadoPago.ExternalPosId))
        return Results.Problem("Configure MercadoPago__AccessToken e MercadoPago__ExternalPosId.", statusCode: 503);

    var order = await mercadoPago.CreateQrOrderAsync(budget);
    var paymentId = order.Transactions?.Payments?.FirstOrDefault()?.Id;
    var updated = await db.GetCollection<Budget>("budgets").FindOneAndUpdateAsync<Budget, Budget>(x => x.Id == id,
        Builders<Budget>.Update.Set(x => x.MercadoPagoOrderId, order.Id).Set(x => x.MercadoPagoPaymentId, paymentId).Set(x => x.MercadoPagoQrData, order.QrData).Set(x => x.PaymentProvider, "mercado-pago-qr-sandbox").Set(x => x.UpdatedAt, DateTimeOffset.UtcNow),
        new FindOneAndUpdateOptions<Budget> { ReturnDocument = ReturnDocument.After });
    return Results.Ok(new { budget = updated, orderId = order.Id, paymentId, qrData = order.QrData, status = order.Status });
});

app.MapPost("/api/payments/webhook", async (HttpRequest request, JsonElement notification, IMongoDatabase db, MercadoPagoClient mercadoPago) =>
{
    var type = notification.ValueKind == JsonValueKind.Object && notification.TryGetProperty("type", out var notificationType)
        ? notificationType.GetString()
        : request.Query["topic"].ToString();
    if (type == "merchant_order") return Results.Ok();
    if (!mercadoPago.IsValidWebhook(request, notification)) return Results.Unauthorized();
    var resourceId = notification.ValueKind == JsonValueKind.Object && notification.TryGetProperty("data", out var data) && data.TryGetProperty("id", out var paymentId)
        ? paymentId.ToString()
        : request.Query["data.id"].ToString();
    if (string.IsNullOrWhiteSpace(resourceId)) resourceId = request.Query["id"].ToString();
    if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(resourceId)) return Results.Ok();

    MercadoPagoResource? payment;
    if (type == "order")
    {
        var order = await mercadoPago.GetOrderAsync(resourceId);
        payment = order is null ? null : new MercadoPagoResource(order.Id, order.Status, order.ExternalReference);
    }
    else
    {
        var directPayment = await mercadoPago.GetPaymentAsync(resourceId);
        payment = directPayment is null ? null : new MercadoPagoResource(directPayment.Id.ToString(), directPayment.Status, directPayment.ExternalReference);
    }
    if (payment is null || string.IsNullOrWhiteSpace(payment.ExternalReference)) return Results.Ok();
    if (!Guid.TryParse(payment.ExternalReference, out var orderId)) return Results.Ok();
    var collection = db.GetCollection<Budget>("budgets");
    var budget = await collection.Find(x => x.OrderId == orderId).FirstOrDefaultAsync();
    if (budget is null) return Results.Ok();
    var paymentStatus = payment.Status == "processed" ? "approved" : payment.Status;
    if (paymentStatus == "approved" && BudgetSaga.CanApprovePayment(budget.Status))
    {
        var pending = SagaOutboxEvent.Create("PAYMENT_APPROVED", new { orderId, budgetId = budget.Id, provider = "mercado-pago-sandbox", paymentId = payment.Id });
        var updated = await collection.FindOneAndUpdateAsync<Budget, Budget>(x => x.Id == budget.Id && (x.Status == BudgetStatuses.Approved || x.Status == BudgetStatuses.PaymentRejected),
            Builders<Budget>.Update.Set(x => x.Status, BudgetStatuses.Paid).Set(x => x.PaymentProvider, "mercado-pago-sandbox").Set(x => x.MercadoPagoPaymentId, payment.Id).Set(x => x.UpdatedAt, DateTimeOffset.UtcNow).Push(x => x.PendingEvents, pending),
            new FindOneAndUpdateOptions<Budget> { ReturnDocument = ReturnDocument.After });
    }
    else if ((paymentStatus is "rejected" or "cancelled" or "canceled" or "expired") && BudgetSaga.CanRejectPayment(budget.Status))
    {
        var pending = SagaOutboxEvent.Create("PAYMENT_REJECTED", new { orderId, budgetId = budget.Id, paymentId = payment.Id });
        var updated = await collection.FindOneAndUpdateAsync<Budget, Budget>(x => x.Id == budget.Id && x.Status == BudgetStatuses.Approved,
            Builders<Budget>.Update.Set(x => x.Status, BudgetStatuses.PaymentRejected).Set(x => x.PaymentProvider, "mercado-pago-sandbox").Set(x => x.MercadoPagoPaymentId, payment.Id).Set(x => x.UpdatedAt, DateTimeOffset.UtcNow).Push(x => x.PendingEvents, pending),
            new FindOneAndUpdateOptions<Budget> { ReturnDocument = ReturnDocument.After });
    }
    return Results.Ok();
});

app.Run();

namespace Oficina.Billing
{
public record RejectBudgetRequest(string Reason);
public record UpdateBudgetAmountRequest(decimal Amount);
public static class BudgetStatuses { public const string PendingApproval = "PENDING_APPROVAL"; public const string Approved = "APPROVED"; public const string Rejected = "REJECTED"; public const string Paid = "PAID"; public const string PaymentRejected = "PAYMENT_REJECTED"; }
public static class BudgetSaga
{
    public static bool CanApprovePayment(string status) => status is BudgetStatuses.Approved or BudgetStatuses.PaymentRejected;
    public static bool CanRejectPayment(string status) => status == BudgetStatuses.Approved;
}
public class Budget
{
    [BsonId] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public Guid OrderId { get; set; }
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
    public string Status { get; set; } = BudgetStatuses.PendingApproval;
    public string? RejectionReason { get; set; }
    public string? PaymentProvider { get; set; }
    public string? MercadoPagoPreferenceId { get; set; }
    public string? MercadoPagoPaymentId { get; set; }
    public string? MercadoPagoOrderId { get; set; }
    public string? MercadoPagoQrData { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    [JsonIgnore] public List<SagaOutboxEvent> PendingEvents { get; set; } = [];
}

public class SagaOutboxEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Type { get; set; } = "";
    public string Body { get; set; } = "";
    public static SagaOutboxEvent Create(string type, object payload) => new() { Type = type, Body = JsonSerializer.Serialize(new { type, payload, occurredAt = DateTimeOffset.UtcNow }) };
}

public sealed class MercadoPagoClient(HttpClient httpClient, IConfiguration configuration)
{
    private const string ApiBaseUrl = "https://api.mercadopago.com";
    public string AccessToken => configuration["MercadoPago:AccessToken"] ?? "";
    public string ExternalPosId => configuration["MercadoPago:ExternalPosId"] ?? "";
    private string WebhookUrl => NormalizeWebhookUrl(configuration["MercadoPago:WebhookUrl"] ?? "");
    private string WebhookSecret => configuration["MercadoPago:WebhookSecret"] ?? "";

    private static string NormalizeWebhookUrl(string value)
    {
        var url = value.Trim();
        while (url.StartsWith("https://https://", StringComparison.OrdinalIgnoreCase))
            url = url["https://".Length..];
        while (url.StartsWith("http://http://", StringComparison.OrdinalIgnoreCase))
            url = url["http://".Length..];
        return url;
    }

    public async Task<MercadoPagoPreference> CreatePreferenceAsync(Budget budget)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBaseUrl}/checkout/preferences");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AccessToken);
        var body = new
        {
            items = new[] { new { id = budget.Id, title = budget.Description, quantity = 1, currency_id = "BRL", unit_price = budget.Amount } },
            external_reference = budget.OrderId.ToString(),
            notification_url = WebhookUrl
        };
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MercadoPagoPreference>()) ?? throw new InvalidOperationException("Resposta vazia do Mercado Pago");
    }

    public async Task<MercadoPagoPayment?> GetPaymentAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBaseUrl}/v1/payments/{Uri.EscapeDataString(id)}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AccessToken);
        using var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<MercadoPagoPayment>();
    }

    public async Task<MercadoPagoOrder> CreateQrOrderAsync(Budget budget)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBaseUrl}/v1/orders");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AccessToken);
        request.Headers.Add("X-Idempotency-Key", Guid.NewGuid().ToString());
        var body = new
        {
            type = "qr",
            total_amount = budget.Amount,
            description = budget.Description,
            external_reference = budget.OrderId.ToString(),
            config = new { qr = new { external_pos_id = ExternalPosId, mode = "dynamic" } },
            transactions = new { payments = new[] { new { amount = budget.Amount } } },
            items = new[] { new { title = budget.Description, unit_price = budget.Amount, quantity = 1, external_code = budget.Id } }
        };
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MercadoPagoOrder>()) ?? throw new InvalidOperationException("Resposta vazia do Mercado Pago");
    }

    public async Task<MercadoPagoOrder?> GetOrderAsync(string id)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBaseUrl}/v1/orders/{Uri.EscapeDataString(id)}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AccessToken);
        using var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<MercadoPagoOrder>();
    }

    public bool IsValidWebhook(HttpRequest request, JsonElement notification)
    {
        if (string.IsNullOrWhiteSpace(WebhookSecret)) return true;
        var signature = request.Headers["x-signature"].ToString();
        var requestId = request.Headers["x-request-id"].ToString();
        if (string.IsNullOrWhiteSpace(signature) && request.Query.ContainsKey("topic") && request.Query.ContainsKey("id")) return true;
        if (notification.ValueKind != JsonValueKind.Object) return false;
        var id = notification.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("id", out var value) ? value.ToString() : null;
        var timestamp = signature.Split(',').FirstOrDefault(x => x.StartsWith("ts="))?.Split('=', 2)[1];
        var receivedHash = signature.Split(',').FirstOrDefault(x => x.StartsWith("v1="))?.Split('=', 2)[1];
        if (string.IsNullOrWhiteSpace(timestamp) || string.IsNullOrWhiteSpace(receivedHash) || string.IsNullOrWhiteSpace(id)) return false;
        var manifest = $"id:{id};request-id:{requestId};ts:{timestamp};";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(WebhookSecret));
        var expectedHash = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expectedHash), Encoding.UTF8.GetBytes(receivedHash));
    }
}

public sealed class MercadoPagoPreference
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("init_point")]
    public string? InitPoint { get; set; }

    [JsonPropertyName("sandbox_init_point")]
    public string? SandboxInitPoint { get; set; }
}
public record MercadoPagoResource(string Id, string Status, string? ExternalReference);
public record MercadoPagoPayment([property: JsonPropertyName("id")] JsonElement Id, [property: JsonPropertyName("status")] string Status, [property: JsonPropertyName("external_reference")] string? ExternalReference);
public record MercadoPagoOrder([property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("status")] string Status, [property: JsonPropertyName("external_reference")] string? ExternalReference, [property: JsonPropertyName("qr_data")] string? QrData, [property: JsonPropertyName("transactions")] MercadoPagoTransactions? Transactions);
public record MercadoPagoTransactions([property: JsonPropertyName("payments")] List<MercadoPagoOrderPayment>? Payments);
public record MercadoPagoOrderPayment([property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("status")] string Status);

public sealed class EventPublisher(IConfiguration configuration)
{
    public Task PublishAsync(string type, string body)
    {
        var settings = configuration.GetSection("RabbitMq"); var factory = new ConnectionFactory { HostName = settings["Host"] ?? "localhost", Port = settings.GetValue("Port", 5672), UserName = settings["User"] ?? "oficina", Password = settings["Password"] ?? "oficina" }; using var connection = factory.CreateConnection(); using var channel = connection.CreateModel(); channel.ExchangeDeclare("oficina.events", ExchangeType.Topic, durable: true);
        channel.QueueDeclare("os-service-events", true, false, false);
        foreach (var key in new[] { "BUDGET_CREATED", "BUDGET_REJECTED", "PAYMENT_APPROVED", "PAYMENT_REJECTED", "EXECUTION_FINISHED", "EXECUTION_FAILED" }) channel.QueueBind("os-service-events", "oficina.events", key);
        channel.ConfirmSelect(); var properties = channel.CreateBasicProperties(); properties.Persistent = true;
        channel.BasicPublish("oficina.events", type, properties, Encoding.UTF8.GetBytes(body)); channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(5)); return Task.CompletedTask;
    }
}

public sealed class BillingOutboxWorker(IMongoDatabase db, EventPublisher events, ILogger<BillingOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var collection = db.GetCollection<Budget>("budgets");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var pending = await collection.Find(Builders<Budget>.Filter.Exists("PendingEvents.0")).Limit(50).ToListAsync(stoppingToken);
                foreach (var budget in pending)
                    foreach (var item in budget.PendingEvents)
                    {
                        await events.PublishAsync(item.Type, item.Body);
                        await collection.UpdateOneAsync(x => x.Id == budget.Id, Builders<Budget>.Update.PullFilter(x => x.PendingEvents, x => x.Id == item.Id), cancellationToken: stoppingToken);
                    }
            }
            catch (Exception ex) { logger.LogError(ex, "Falha ao publicar outbox de cobranca; sera tentado novamente"); }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}

public sealed class OsCreatedConsumer(IConfiguration configuration, IMongoDatabase db, ILogger<OsCreatedConsumer> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = configuration.GetSection("RabbitMq"); var factory = new ConnectionFactory { HostName = settings["Host"] ?? "localhost", Port = settings.GetValue("Port", 5672), UserName = settings["User"] ?? "oficina", Password = settings["Password"] ?? "oficina" }; var connection = factory.CreateConnection(); var channel = connection.CreateModel(); channel.ExchangeDeclare("oficina.events", ExchangeType.Topic, durable: true); var queue = channel.QueueDeclare("billing-service-events", true, false, false).QueueName; channel.QueueBind(queue, "oficina.events", "OS_CREATED"); channel.BasicQos(0, 1, false);
        var consumer = new EventingBasicConsumer(channel);
        consumer.Received += async (_, args) =>
        {
            try
            {
                using var document = JsonDocument.Parse(args.Body.ToArray()); var payload = document.RootElement.GetProperty("payload"); var orderId = payload.GetProperty("orderId").GetGuid(); var description = payload.GetProperty("description").GetString() ?? "Servico mecanico"; var collection = db.GetCollection<Budget>("budgets"); var existing = await collection.Find(x => x.OrderId == orderId).FirstOrDefaultAsync(stoppingToken);
                if (existing is null)
                {
                    var budget = new Budget { OrderId = orderId, Description = description, Amount = 0 };
                    budget.PendingEvents.Add(SagaOutboxEvent.Create("BUDGET_CREATED", new { orderId, budgetId = budget.Id, description }));
                    await collection.InsertOneAsync(budget, cancellationToken: stoppingToken);
                }
                channel.BasicAck(args.DeliveryTag, false);
            }
            catch (JsonException ex) { logger.LogError(ex, "Evento OS_CREATED malformado; descartando"); channel.BasicReject(args.DeliveryTag, false); }
            catch (Exception ex) { logger.LogError(ex, "Falha ao gerar orcamento"); channel.BasicNack(args.DeliveryTag, false, true); }
        };
        channel.BasicConsume(queue, false, consumer); return Task.CompletedTask;
    }
}

}

public partial class Program { }
