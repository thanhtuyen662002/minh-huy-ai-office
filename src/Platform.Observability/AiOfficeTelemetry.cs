using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace MinhHuy.AIOffice.Platform.Observability;

public static class AiOfficeTelemetry
{
    public const string ActivitySourceName = "MinhHuy.AIOffice";
    public const string MeterName = "MinhHuy.AIOffice";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> HttpRequests = Meter.CreateCounter<long>(
        "aioffice.http.requests",
        description: "Completed Core API HTTP requests.");

    public static readonly Histogram<double> HttpRequestDuration = Meter.CreateHistogram<double>(
        "aioffice.http.request.duration",
        unit: "ms",
        description: "Core API HTTP request duration.");

    public static readonly Counter<long> WorkerLifecycleEvents = Meter.CreateCounter<long>(
        "aioffice.worker.lifecycle.events",
        description: "Agent worker lifecycle events.");

    public static void RecordHttpRequest(int statusCode, TimeSpan duration)
    {
        var tags = new TagList
        {
            { "http.response.status_code", statusCode }
        };

        HttpRequests.Add(1, tags);
        HttpRequestDuration.Record(duration.TotalMilliseconds, tags);
    }
}
