using System.Text.Json;

namespace Setup.Core;

public static class RuntimeDiagnostics
{
    private static readonly Dictionary<string, RuntimeService> Known = new(StringComparer.Ordinal)
    {
        ["sql"] = RuntimeService.Sql, ["identity-db"] = RuntimeService.IdentityDatabase,
        ["identity"] = RuntimeService.Identity, ["rabbitmq"] = RuntimeService.RabbitMq, ["redis"] = RuntimeService.Redis,
        ["jaeger"] = RuntimeService.Jaeger, ["otel-collector"] = RuntimeService.Telemetry,
        ["core-api"] = RuntimeService.CoreApi, ["agent-worker"] = RuntimeService.AgentWorker,
        ["web"] = RuntimeService.Web, ["bootstrap"] = RuntimeService.Bootstrap
    };

    public static IReadOnlyList<ServiceDiagnostic> Parse(string output)
    {
        var values = new Dictionary<RuntimeService, ServiceDiagnostic>();
        try
        {
            if (output.TrimStart().StartsWith('['))
            {
                using var document = JsonDocument.Parse(output);
                foreach (var entry in document.RootElement.EnumerateArray()) Add(entry, values);
            }
            else
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    using var document = JsonDocument.Parse(line);
                    Add(document.RootElement, values);
                }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        {
            values.Clear();
        }
        return Known.Values.Select(service => values.TryGetValue(service, out var value)
            ? value : new ServiceDiagnostic(service, ServiceState.Missing, ServiceHealth.Unknown, 0)).ToArray();
    }

    private static void Add(JsonElement entry, Dictionary<RuntimeService, ServiceDiagnostic> values)
    {
        var name = entry.GetProperty("Service").GetString();
        if (name is null || !Known.TryGetValue(name, out var service)) return;
        var state = entry.GetProperty("State").GetString() switch
        {
            "created" => ServiceState.Created, "running" => ServiceState.Running, "exited" => ServiceState.Exited,
            "restarting" => ServiceState.Restarting, "paused" => ServiceState.Paused,
            "dead" => ServiceState.Dead, "removing" => ServiceState.Removing, _ => ServiceState.Unknown
        };
        var health = (entry.TryGetProperty("Health", out var property) ? property.GetString() : "") switch
        {
            "" => ServiceHealth.None, "starting" => ServiceHealth.Starting,
            "healthy" => ServiceHealth.Healthy, "unhealthy" => ServiceHealth.Unhealthy, _ => ServiceHealth.Unknown
        };
        var exit = entry.GetProperty("ExitCode").GetInt32();
        if (exit is < 0 or > 255 || !values.TryAdd(service, new ServiceDiagnostic(service, state, health, exit)))
            throw new InvalidOperationException("Invalid service status.");
    }
}
