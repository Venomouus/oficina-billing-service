using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<HostOptions>(options => options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost);
builder.Services.AddSingleton(sp => { var section = sp.GetRequiredService<IConfiguration>().GetSection("Mongo"); return new MongoClient(section["ConnectionString"] ?? "mongodb://localhost:27017").GetDatabase(section["Database"] ?? "oficina_billing"); });
builder.Services.AddSingleton<EventPublisher>();
builder.Services.AddHttpClient<MercadoPagoClient>();
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
    var budget = await collection.FindOneAndUpdateAsync<Budget>(x => x.Id == id && x.Status == BudgetStatuses.PendingApproval, Builders<Budget>.Update.Set(x => x.Status, BudgetStatuses.Approved).Set(x => x.UpdatedAt, DateTimeOffset.UtcNow), new FindOneAndUpdateOptions<Budget> { ReturnDocument = ReturnDocument.After });
    if (budget is null) return Results.NotFound(new { error = "orcamento inexistente ou fora de aprovacao" });
    await events.PublishAsync("BUDGET_APPROVED", new { orderId = budget.OrderId, budgetId = budget.Id });
    return Results.Ok(budget);
});

app.MapPost("/api/budgets/{id}/reject", async (string id, RejectBudgetRequest request, IMongoDatabase db, EventPublisher events) =>
{
    var collection = db.GetCollection<Budget>("budgets");
    var budget = await collection.FindOneAndUpdateAsync<Budget>(x => x.Id == id && x.Status == BudgetStatuses.PendingApproval, Builders<Budget>.Update.Set(x => x.Status, BudgetStatuses.Rejected).Set(x => x.RejectionReason, request.Reason).Set(x => x.UpdatedAt, DateTimeOffset.UtcNow), new FindOneAndUpdateOptions<Budget> { ReturnDocument = ReturnDocument.After });
    if (budget is null) return Results.NotFound();
    await events.PublishAsync("BUDGET_REJECTED", new { orderId = budget.OrderId, budgetId = budget.Id, reason = request.Reason });
    return Results.Ok(budget);
});

app.MapPost("/api/budgets/{id}/payment", async (string id, IMongoDatabase db, EventPublisher events) =>
{
    var collection = db.GetCollection<Budget>("budgets");
    var budget = await collection.FindOneAndUpdateAsync<Budget>(x => x.Id == id && x.Status == BudgetStatuses.Approved, Builders<Budget>.Update.Set(x => x.Status, BudgetStatuses.Paid).Set(x => x.PaymentProvider, "mercado-pago-sandbox").Set(x => x.UpdatedAt, DateTimeOffset.UtcNow), new FindOneAndUpdateOptions<Budget> { ReturnDocument = ReturnDocument.After });
    if (budget is null) return Results.NotFound(new { error = "orcamento inexistente ou nao aprovado" });
    await events.PublishAsync("PAYMENT_APPROVED", new { orderId = budget.OrderId, budgetId = budget.Id, provider = budget.PaymentProvider });
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

app.MapPost("/api/payments/webhook", async (HttpRequest request, JsonElement notification, IMongoDatabase db, MercadoPagoClient mercadoPago, EventPublisher events) =>
{
    if (!mercadoPago.IsValidWebhook(request, notification)) return Results.Unauthorized();
    if (!notification.TryGetProperty("type", out var type)) return Results.Ok();
    if (!notification.TryGetProperty("data", out var data) || !data.TryGetProperty("id", out var paymentId)) return Results.BadRequest();

    var resourceId = paymentId.GetString() ?? "";
    MercadoPagoResource? payment;
    if (type.GetString() == "order")
    {
        var order = await mercadoPago.GetOrderAsync(resourceId);
        payment = order is null ? null : new MercadoPagoResource(order.Id, order.Status, order.ExternalReference);
    }
    else
    {
        var directPayment = await mercadoPago.GetPaymentAsync(resourceId);
        payment = directPayment is null ? null : new MercadoPagoResource(directPayment.Id, directPayment.Status, directPayment.ExternalReference);
    }
    if (payment is null || string.IsNullOrWhiteSpace(payment.ExternalReference)) return Results.Ok();
    var collection = db.GetCollection<Budget>("budgets");
    var budget = await collection.Find(x => x.OrderId.ToString() == payment.ExternalReference).FirstOrDefaultAsync();
    if (budget is null) return Results.Ok();
    var paymentStatus = payment.Status == "processed" ? "approved" : payment.Status;
    if (paymentStatus == "approved" && budget.Status != BudgetStatuses.Paid)
    {
        var updated = await collection.FindOneAndUpdateAsync<Budget, Budget>(x => x.Id == budget.Id && x.Status != BudgetStatuses.Paid,
            Builders<Budget>.Update.Set(x => x.Status, BudgetStatuses.Paid).Set(x => x.PaymentProvider, "mercado-pago-sandbox").Set(x => x.MercadoPagoPaymentId, payment.Id).Set(x => x.UpdatedAt, DateTimeOffset.UtcNow),
            new FindOneAndUpdateOptions<Budget> { ReturnDocument = ReturnDocument.After });
        if (updated is not null) await events.PublishAsync("PAYMENT_APPROVED", new { orderId = updated.OrderId, budgetId = updated.Id, provider = updated.PaymentProvider, paymentId = updated.MercadoPagoPaymentId });
    }
    else if (paymentStatus is "rejected" or "cancelled" or "canceled" or "expired")
    {
        var updated = await collection.FindOneAndUpdateAsync<Budget, Budget>(x => x.Id == budget.Id && x.Status != BudgetStatuses.Paid,
            Builders<Budget>.Update.Set(x => x.Status, BudgetStatuses.PaymentRejected).Set(x => x.PaymentProvider, "mercado-pago-sandbox").Set(x => x.MercadoPagoPaymentId, payment.Id).Set(x => x.UpdatedAt, DateTimeOffset.UtcNow),
            new FindOneAndUpdateOptions<Budget> { ReturnDocument = ReturnDocument.After });
        if (updated is not null) await events.PublishAsync("PAYMENT_REJECTED", new { orderId = updated.OrderId, budgetId = updated.Id, paymentId });
    }
    return Results.Ok();
});

app.Run();

public record RejectBudgetRequest(string Reason);
public static class BudgetStatuses { public const string PendingApproval = "PENDING_APPROVAL"; public const string Approved = "APPROVED"; public const string Rejected = "REJECTED"; public const string Paid = "PAID"; public const string PaymentRejected = "PAYMENT_REJECTED"; }
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
}

public sealed class MercadoPagoClient(HttpClient httpClient, IConfiguration configuration)
{
    private const string ApiBaseUrl = "https://api.mercadopago.com";
    public string AccessToken => configuration["MercadoPago:AccessToken"] ?? "";
    public string ExternalPosId => configuration["MercadoPago:ExternalPosId"] ?? "";
    private string WebhookUrl => configuration["MercadoPago:WebhookUrl"] ?? "";
    private string WebhookSecret => configuration["MercadoPago:WebhookSecret"] ?? "";

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
        var id = notification.TryGetProperty("data", out var data) && data.TryGetProperty("id", out var value) ? value.GetString() : null;
        var timestamp = signature.Split(',').FirstOrDefault(x => x.StartsWith("ts="))?.Split('=', 2)[1];
        var receivedHash = signature.Split(',').FirstOrDefault(x => x.StartsWith("v1="))?.Split('=', 2)[1];
        if (string.IsNullOrWhiteSpace(timestamp) || string.IsNullOrWhiteSpace(receivedHash) || string.IsNullOrWhiteSpace(id)) return false;
        var manifest = $"id:{id};request-id:{requestId};ts:{timestamp};";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(WebhookSecret));
        var expectedHash = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expectedHash), Encoding.UTF8.GetBytes(receivedHash));
    }
}

public record MercadoPagoPreference(string Id, string? InitPoint, string? SandboxInitPoint);
public record MercadoPagoResource(string Id, string Status, string? ExternalReference);
public record MercadoPagoPayment([property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("status")] string Status, [property: JsonPropertyName("external_reference")] string? ExternalReference);
public record MercadoPagoOrder([property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("status")] string Status, [property: JsonPropertyName("external_reference")] string? ExternalReference, [property: JsonPropertyName("qr_data")] string? QrData, [property: JsonPropertyName("transactions")] MercadoPagoTransactions? Transactions);
public record MercadoPagoTransactions([property: JsonPropertyName("payments")] List<MercadoPagoOrderPayment>? Payments);
public record MercadoPagoOrderPayment([property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("status")] string Status);

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
