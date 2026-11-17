using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Oficina.Billing;

public static class Observability
{
    public const string ServiceName = "billing-service";
    public static readonly ActivitySource Activities = new(ServiceName);
    public static readonly Meter Meter = new(ServiceName);
    public static readonly Counter<long> Published = Meter.CreateCounter<long>("oficina.saga.events.published");
    public static readonly Counter<long> Consumed = Meter.CreateCounter<long>("oficina.saga.events.consumed");
    public static readonly Counter<long> Failed = Meter.CreateCounter<long>("oficina.saga.events.failed");

    public static void Configure(WebApplicationBuilder builder)
    {
        builder.Logging.AddJsonConsole();
        var export = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName))
            .WithTracing(tracing =>
            {
                tracing.AddSource(ServiceName).AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();
                if (export) tracing.AddOtlpExporter();
            })
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(ServiceName).AddAspNetCoreInstrumentation();
                if (export) metrics.AddOtlpExporter();
            });
        if (export) builder.Logging.AddOpenTelemetry(options => options.AddOtlpExporter());
    }
}
