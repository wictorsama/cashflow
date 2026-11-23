using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace CashFlow.Observability;

public static class ObservabilityExtensions
{
    private static readonly double[] SecondsBuckets = [0.05, 0.1, 0.25, 0.5, 1, 2, 3, 5, 8, 10, 15, 30, 60, 120];

    /// <summary>
    /// Structured logs, traces and metrics for one service.
    /// Logs are JSON on stdout, carrying the trace and span ids so a log line can be found from a trace and back.
    /// Traces go to an OTLP/gRPC endpoint (Jaeger) and metrics to an OTLP/HTTP endpoint (Prometheus). When an endpoint
    /// is not configured (tests, local runs) the signal is still produced in-process but not exported.
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="serviceName">Shown in traces and used as the metrics "job".</param>
    /// <param name="activitySources">Custom <c>ActivitySource</c> names to record.</param>
    /// <param name="meters">Custom <c>Meter</c> names to export.</param>
    public static WebApplicationBuilder AddCashFlowObservability(
        this WebApplicationBuilder builder,
        string serviceName,
        IEnumerable<string>? activitySources = null,
        IEnumerable<string>? meters = null)
    {
        builder.Logging.Configure(options => options.ActivityTrackingOptions =
            ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId | ActivityTrackingOptions.ParentId);
        builder.Logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.UseUtcTimestamp = true;
        });

        var tracesEndpoint = builder.Configuration["Otel:TracesEndpoint"];
        var metricsEndpoint = builder.Configuration["Otel:MetricsEndpoint"];

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName, serviceVersion: "0.1.0")
                .AddAttributes([new KeyValuePair<string, object>("service.instance.id", Environment.MachineName)]))
            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation(options =>
                        // Probes would drown the interesting traces.
                        options.Filter = context => !context.Request.Path.StartsWithSegments("/health", StringComparison.Ordinal))
                    .AddHttpClientInstrumentation()
                    .AddSource("Npgsql") // database commands, emitted by the driver itself
                    .AddSource([.. activitySources ?? []]);

                if (!string.IsNullOrWhiteSpace(tracesEndpoint))
                {
                    tracing.AddOtlpExporter(options =>
                    {
                        options.Endpoint = new Uri(tracesEndpoint);
                        options.Protocol = OtlpExportProtocol.Grpc;
                    });
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter("Npgsql")
                    .AddMeter([.. meters ?? []])
                    // The default buckets (0, 5, 10, 25 s...) are too coarse to tell a 3 s freshness from a 14 s one.
                    .AddView("cashflow.event.freshness", new ExplicitBucketHistogramConfiguration { Boundaries = SecondsBuckets })
                    .AddView("cashflow.outbox.publish_delay", new ExplicitBucketHistogramConfiguration { Boundaries = SecondsBuckets });

                if (!string.IsNullOrWhiteSpace(metricsEndpoint))
                {
                    metrics.AddOtlpExporter((exporter, reader) =>
                    {
                        exporter.Endpoint = new Uri(metricsEndpoint);
                        exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
                        reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 10_000;
                    });
                }
            });

        return builder;
    }
}
