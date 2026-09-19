using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace MinhHuy.AIOffice.Platform.Observability;

public static class ObservabilityExtensions
{
    public static IServiceCollection AddAiOfficeObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName,
        bool includeAspNetCoreInstrumentation = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var endpoint = GetOtlpEndpoint(configuration);

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(AiOfficeTelemetry.ActivitySourceName)
                    .AddHttpClientInstrumentation();

                if (includeAspNetCoreInstrumentation)
                {
                    tracing.AddAspNetCoreInstrumentation();
                }

                if (endpoint is not null)
                {
                    tracing.AddOtlpExporter(options => options.Endpoint = endpoint);
                }
            })
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(AiOfficeTelemetry.MeterName);

                if (endpoint is not null)
                {
                    metrics.AddOtlpExporter(options => options.Endpoint = endpoint);
                }
            });

        return services;
    }

    public static ILoggingBuilder AddAiOfficeOpenTelemetryLogging(
        this ILoggingBuilder logging,
        IConfiguration configuration,
        string serviceName)
    {
        ArgumentNullException.ThrowIfNull(logging);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var endpoint = GetOtlpEndpoint(configuration);

        logging.AddOpenTelemetry(options =>
        {
            options.IncludeFormattedMessage = true;
            options.IncludeScopes = true;
            options.ParseStateValues = true;
            options.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName));

            if (endpoint is not null)
            {
                options.AddOtlpExporter(exporter => exporter.Endpoint = endpoint);
            }
        });

        return logging;
    }

    private static Uri? GetOtlpEndpoint(IConfiguration configuration)
    {
        var value = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttp &&
             endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                "OTEL_EXPORTER_OTLP_ENDPOINT must be an absolute HTTP or HTTPS URI.");
        }

        return endpoint;
    }
}
