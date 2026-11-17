using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Testcontainers.MongoDb;
using DotNet.Testcontainers.Builders;
using Xunit;

public sealed class MongoFixture : IAsyncLifetime
{
    public MongoDbContainer Container { get; } = new MongoDbBuilder().WithImage("mongo:7").Build();
    public Task InitializeAsync() => Container.StartAsync();
    public Task DisposeAsync() => Container.DisposeAsync().AsTask();
}

public sealed class EndpointTests(MongoFixture mongo) : IClassFixture<MongoFixture>, IAsyncLifetime
{
    private BillingFactory factory = null!;
    private HttpClient http = null!;
    private IMongoCollection<Budget> budgets = null!;

    public Task InitializeAsync()
    {
        var db = new MongoClient(mongo.Container.GetConnectionString()).GetDatabase("billing_test_" + Guid.NewGuid().ToString("N"));
        budgets = db.GetCollection<Budget>("budgets");
        factory = new BillingFactory(db);
        http = factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        http.Dispose();
        await factory.DisposeAsync();
    }

    private async Task<Budget> Seed(string status = BudgetStatuses.PendingApproval)
    {
        var budget = new Budget { OrderId = Guid.NewGuid(), Description = "Brake repair", Amount = 100m, Status = status };
        await budgets.InsertOneAsync(budget);
        return budget;
    }

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");
    private async Task<JsonDocument> Read(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    [Fact]
    public async Task Health_and_budget_reads_return_persisted_data()
    {
        var older = await Seed();
        var newer = await Seed();
        await budgets.UpdateOneAsync(x => x.Id == older.Id, Builders<Budget>.Update.Set(x => x.CreatedAt, DateTimeOffset.UtcNow.AddDays(-1)));

        var health = await http.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using (var body = await Read(health)) Assert.Equal("UP", body.RootElement.GetProperty("status").GetString());
        using (var list = await Read(await http.GetAsync("/api/budgets")))
        {
            Assert.Equal(2, list.RootElement.GetArrayLength());
            Assert.Equal(newer.Id, list.RootElement[0].GetProperty("id").GetString());
        }
        using (var one = await Read(await http.GetAsync($"/api/budgets/{older.Id}")))
            Assert.Equal(older.OrderId, one.RootElement.GetProperty("orderId").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/budgets/missing")).StatusCode);
    }

    [Fact]
    public async Task Amount_and_approval_enforce_state_and_create_one_outbox_event()
    {
        var budget = await Seed();
        var path = $"/api/budgets/{budget.Id}";
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PatchAsync(path + "/amount", Json("""{"amount":0}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PatchAsync("/api/budgets/missing/amount", Json("""{"amount":45}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.PatchAsync(path + "/amount", Json("""{"amount":225.50}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync(path + "/approve", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync(path + "/approve", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PatchAsync(path + "/amount", Json("""{"amount":7}"""))).StatusCode);
        var stored = await budgets.Find(x => x.Id == budget.Id).SingleAsync();
        Assert.Equal(225.50m, stored.Amount);
        Assert.Equal(BudgetStatuses.Approved, stored.Status);
        Assert.NotNull(stored.UpdatedAt);
        Assert.Single(stored.PendingEvents);
        Assert.Equal("BUDGET_APPROVED", stored.PendingEvents[0].Type);
        using var eventBody = JsonDocument.Parse(stored.PendingEvents[0].Body);
        Assert.Equal(budget.OrderId, eventBody.RootElement.GetProperty("payload").GetProperty("orderId").GetGuid());
    }

    [Fact]
    public async Task Rejection_and_local_payment_persist_transitions_and_outbox()
    {
        var rejected = await Seed();
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync($"/api/budgets/{rejected.Id}/reject", Json("""{"reason":"too expensive"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync($"/api/budgets/{rejected.Id}/reject", Json("""{"reason":"again"}"""))).StatusCode);
        var storedRejected = await budgets.Find(x => x.Id == rejected.Id).SingleAsync();
        Assert.Equal("too expensive", storedRejected.RejectionReason);
        Assert.Equal("BUDGET_REJECTED", Assert.Single(storedRejected.PendingEvents).Type);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync($"/api/budgets/{rejected.Id}/payment", null)).StatusCode);

        var approved = await Seed(BudgetStatuses.Approved);
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync($"/api/budgets/{approved.Id}/payment", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync($"/api/budgets/{approved.Id}/payment", null)).StatusCode);
        var paid = await budgets.Find(x => x.Id == approved.Id).SingleAsync();
        Assert.Equal(BudgetStatuses.Paid, paid.Status);
        Assert.Equal("mercado-pago-sandbox", paid.PaymentProvider);
        Assert.Equal("PAYMENT_APPROVED", Assert.Single(paid.PendingEvents).Type);
    }

    [Fact]
    public async Task Preference_and_qr_endpoints_call_fake_provider_and_save_response()
    {
        var budget = await Seed(BudgetStatuses.Approved);
        var path = $"/api/budgets/{budget.Id}/payment";
        factory.ProviderReply = """{"id":"pref-1","init_point":"https://live.test","sandbox_init_point":"https://sandbox.test"}""";
        using (var response = await Read(await http.PostAsync(path + "/preference", null)))
        {
            Assert.Equal("pref-1", response.RootElement.GetProperty("preferenceId").GetString());
            Assert.Equal("https://sandbox.test", response.RootElement.GetProperty("checkoutUrl").GetString());
        }
        Assert.Equal("pref-1", (await budgets.Find(x => x.Id == budget.Id).SingleAsync()).MercadoPagoPreferenceId);

        factory.ProviderReply = """{"id":"order-1","status":"pending","qr_data":"qr-contents","transactions":{"payments":[{"id":"payment-1","status":"pending"}]}}""";
        using (var response = await Read(await http.PostAsync(path + "/qr", null)))
        {
            Assert.Equal("order-1", response.RootElement.GetProperty("orderId").GetString());
            Assert.Equal("qr-contents", response.RootElement.GetProperty("qrData").GetString());
        }
        var updated = await budgets.Find(x => x.Id == budget.Id).SingleAsync();
        Assert.Equal("payment-1", updated.MercadoPagoPaymentId);
        Assert.Equal("mercado-pago-qr-sandbox", updated.PaymentProvider);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync("/api/budgets/missing/payment/preference", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync("/api/budgets/missing/payment/qr", null)).StatusCode);
    }

    [Theory]
    [InlineData("approved", "PAID", "PAYMENT_APPROVED")]
    [InlineData("rejected", "PAYMENT_REJECTED", "PAYMENT_REJECTED")]
    [InlineData("processed", "PAID", "PAYMENT_APPROVED")]
    public async Task Webhook_updates_budget_and_is_idempotent(string status, string expected, string eventType)
    {
        var budget = await Seed(BudgetStatuses.Approved);
        factory.ProviderReply = """{"id":123,"status":"STATUS","external_reference":"ORDER"}"""
            .Replace("STATUS", status).Replace("ORDER", budget.OrderId.ToString());
        var notification = Json("""{"type":"payment","data":{"id":"123"}}""");
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync("/api/payments/webhook", notification)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync("/api/payments/webhook", Json("""{"type":"payment","data":{"id":"123"}}"""))).StatusCode);
        var stored = await budgets.Find(x => x.Id == budget.Id).SingleAsync();
        Assert.Equal(expected, stored.Status);
        Assert.Equal(eventType, Assert.Single(stored.PendingEvents).Type);
        Assert.Equal("123", stored.MercadoPagoPaymentId);
    }

    [Fact]
    public async Task Order_webhook_and_recovery_from_rejected_payment()
    {
        var budget = await Seed(BudgetStatuses.PaymentRejected);
        factory.ProviderReply = """{"id":"order-1","status":"processed","external_reference":"ORDER"}""".Replace("ORDER", budget.OrderId.ToString());
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync("/api/payments/webhook", Json("""{"type":"order","data":{"id":"order-1"}}"""))).StatusCode);
        var stored = await budgets.Find(x => x.Id == budget.Id).SingleAsync();
        Assert.Equal(BudgetStatuses.Paid, stored.Status);
        Assert.Equal("order-1", stored.MercadoPagoPaymentId);
        Assert.Equal("PAYMENT_APPROVED", Assert.Single(stored.PendingEvents).Type);
    }

    [Fact]
    public async Task Webhook_ignores_unusable_notifications_and_unknown_orders()
    {
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync("/api/payments/webhook", Json("""{"type":"merchant_order"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync("/api/payments/webhook", Json("{}"))).StatusCode);
        factory.ProviderReply = """{"id":1,"status":"approved","external_reference":"invalid"}""";
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync("/api/payments/webhook", Json("""{"type":"payment","data":{"id":"1"}}"""))).StatusCode);
        factory.ProviderReply = """{"id":1,"status":"approved","external_reference":"ORDER"}""".Replace("ORDER", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync("/api/payments/webhook", Json("""{"type":"payment","data":{"id":"1"}}"""))).StatusCode);
        factory.ProviderStatus = HttpStatusCode.NotFound;
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync("/api/payments/webhook", Json("""{"type":"payment","data":{"id":"1"}}"""))).StatusCode);
    }

    private sealed class BillingFactory(IMongoDatabase database) : WebApplicationFactory<Program>
    {
        public string ProviderReply { get; set; } = "{}";
        public HttpStatusCode ProviderStatus { get; set; } = HttpStatusCode.OK;
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MercadoPago:AccessToken"] = "fake-token", ["MercadoPago:ExternalPosId"] = "fake-pos"
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IMongoDatabase>();
                services.AddSingleton(database);
                services.RemoveAll<IHostedService>();
                services.AddHttpClient<MercadoPagoClient>().ConfigurePrimaryHttpMessageHandler(() => new ProviderHandler(this));
            });
        }
        private sealed class ProviderHandler(BillingFactory owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(owner.ProviderStatus) { Content = new StringContent(owner.ProviderReply, Encoding.UTF8, "application/json") });
        }
    }
}
