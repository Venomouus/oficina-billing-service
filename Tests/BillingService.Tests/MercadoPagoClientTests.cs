using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

public class MercadoPagoClientTests
{
    private static readonly Guid OrderId = Guid.Parse("c541d22c-e5cd-4a1c-8326-f8e08676e4ba");

    private static (MercadoPagoClient Client, RecordingHandler Handler) Create(string? secret = null, string? webhookUrl = null)
    {
        var handler = new RecordingHandler();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MercadoPago:AccessToken"] = "test-token",
            ["MercadoPago:ExternalPosId"] = "POS-42",
            ["MercadoPago:WebhookUrl"] = webhookUrl ?? "https://https://example.test/hook",
            ["MercadoPago:WebhookSecret"] = secret
        }).Build();
        return (new MercadoPagoClient(new HttpClient(handler), config), handler);
    }

    private static Budget Budget() => new() { Id = "budget-123", OrderId = OrderId, Description = "Brake service", Amount = 125.75m };

    [Fact]
    public async Task Preference_sends_checkout_contract_and_reads_sandbox_url()
    {
        var (client, handler) = Create();
        handler.Respond(HttpStatusCode.OK, """{"id":"pref-1","init_point":"https://live.test","sandbox_init_point":"https://sandbox.test"}""");

        var result = await client.CreatePreferenceAsync(Budget());

        Assert.Equal("pref-1", result.Id);
        Assert.Equal("https://sandbox.test", result.SandboxInitPoint);
        Assert.Equal("POST", handler.Method);
        Assert.Equal("https://api.mercadopago.com/checkout/preferences", handler.Url);
        Assert.Equal("Bearer test-token", handler.Authorization);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(OrderId.ToString(), body.RootElement.GetProperty("external_reference").GetString());
        Assert.Equal("https://example.test/hook", body.RootElement.GetProperty("notification_url").GetString());
        var item = body.RootElement.GetProperty("items")[0];
        Assert.Equal("budget-123", item.GetProperty("id").GetString());
        Assert.Equal("Brake service", item.GetProperty("title").GetString());
        Assert.Equal(125.75m, item.GetProperty("unit_price").GetDecimal());
        Assert.Equal("BRL", item.GetProperty("currency_id").GetString());
    }

    [Theory]
    [InlineData("http://example.test/hook")]
    [InlineData("not-a-url")]
    public async Task Preference_rejects_insecure_or_invalid_webhook_url(string webhookUrl)
    {
        var (client, handler) = Create(webhookUrl: webhookUrl);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CreatePreferenceAsync(Budget()));
        Assert.Null(handler.Url);
    }

    [Fact]
    public async Task Qr_order_sends_pos_and_idempotency_key_and_reads_payment()
    {
        var (client, handler) = Create();
        handler.Respond(HttpStatusCode.OK, """{"id":"order-1","status":"processed","external_reference":"x","qr_data":"qr-text","transactions":{"payments":[{"id":"payment-1","status":"approved"}]}}""");

        var result = await client.CreateQrOrderAsync(Budget());

        Assert.Equal("payment-1", result.Transactions!.Payments![0].Id);
        Assert.Equal("qr-text", result.QrData);
        Assert.Equal("https://api.mercadopago.com/v1/orders", handler.Url);
        Assert.True(Guid.TryParse(handler.IdempotencyKey, out _));
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("qr", body.RootElement.GetProperty("type").GetString());
        Assert.Equal(125.75m, body.RootElement.GetProperty("total_amount").GetDecimal());
        Assert.Equal("POS-42", body.RootElement.GetProperty("config").GetProperty("qr").GetProperty("external_pos_id").GetString());
        Assert.Equal(OrderId.ToString(), body.RootElement.GetProperty("external_reference").GetString());
        Assert.Equal("budget-123", body.RootElement.GetProperty("items")[0].GetProperty("external_code").GetString());
    }

    [Theory]
    [InlineData("payment", "a/b", "https://api.mercadopago.com/v1/payments/a%2Fb")]
    [InlineData("order", "a/b", "https://api.mercadopago.com/v1/orders/a%2Fb")]
    public async Task Lookup_escapes_resource_ids_and_deserializes_status(string kind, string id, string url)
    {
        var (client, handler) = Create();
        handler.Respond(HttpStatusCode.OK, """{"id":"123","status":"approved","external_reference":"order-ref"}""");
        if (kind == "payment")
            Assert.Equal("approved", (await client.GetPaymentAsync(id))!.Status);
        else
            Assert.Equal("order-ref", (await client.GetOrderAsync(id))!.ExternalReference);
        Assert.Equal(url, handler.Url);
        Assert.Equal("Bearer test-token", handler.Authorization);
    }

    [Fact]
    public async Task Lookup_returns_null_for_blank_payment_or_failed_http()
    {
        var (client, handler) = Create();
        Assert.Null(await client.GetPaymentAsync(" "));
        Assert.Equal(0, handler.Calls);
        handler.Respond(HttpStatusCode.NotFound, "{}");
        Assert.Null(await client.GetPaymentAsync("missing"));
        Assert.Null(await client.GetOrderAsync("missing"));
    }

    [Fact]
    public async Task Create_operations_surface_provider_errors_and_empty_replies()
    {
        var (client, handler) = Create();
        handler.Respond(HttpStatusCode.BadRequest, "{}");
        await Assert.ThrowsAsync<HttpRequestException>(() => client.CreatePreferenceAsync(Budget()));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.CreateQrOrderAsync(Budget()));
        handler.Respond(HttpStatusCode.OK, "null");
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CreatePreferenceAsync(Budget()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CreateQrOrderAsync(Budget()));
    }

    [Fact]
    public void Webhook_signature_accepts_valid_hmac_and_rejects_tampering()
    {
        var (client, _) = Create("secret");
        var request = new DefaultHttpContext().Request;
        request.Headers["x-request-id"] = "request-1";
        request.Headers["x-signature"] = "ts=123,v1=" + Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("secret"), Encoding.UTF8.GetBytes("id:pay-1;request-id:request-1;ts:123;"))).ToLowerInvariant();
        using var body = JsonDocument.Parse("""{"data":{"id":"pay-1"}}""");
        Assert.True(client.IsValidWebhook(request, body.RootElement));
        using var tampered = JsonDocument.Parse("""{"data":{"id":"pay-2"}}""");
        Assert.False(client.IsValidWebhook(request, tampered.RootElement));
        request.Headers["x-signature"] = "ts=123";
        Assert.False(client.IsValidWebhook(request, body.RootElement));
        request.Headers["x-signature"] = "v1=bad";
        Assert.False(client.IsValidWebhook(request, body.RootElement));
    }

    [Fact]
    public void Webhook_allows_legacy_topic_and_unsigned_configuration()
    {
        var (signed, _) = Create("secret");
        var request = new DefaultHttpContext().Request;
        request.QueryString = new QueryString("?topic=payment&id=123");
        using var body = JsonDocument.Parse("{}");
        Assert.True(signed.IsValidWebhook(request, body.RootElement));
        request.QueryString = QueryString.Empty;
        Assert.False(signed.IsValidWebhook(request, body.RootElement));
        var (unsigned, _) = Create();
        Assert.True(unsigned.IsValidWebhook(request, body.RootElement));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private HttpStatusCode status = HttpStatusCode.OK;
        private string response = "{}";
        public int Calls { get; private set; }
        public string? Method { get; private set; }
        public string? Url { get; private set; }
        public string? Authorization { get; private set; }
        public string? IdempotencyKey { get; private set; }
        public string? Body { get; private set; }
        public void Respond(HttpStatusCode code, string json) { status = code; response = json; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Method = request.Method.Method;
            Url = request.RequestUri!.AbsoluteUri;
            Authorization = request.Headers.Authorization?.ToString();
            IdempotencyKey = request.Headers.TryGetValues("X-Idempotency-Key", out var values) ? values.Single() : null;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
