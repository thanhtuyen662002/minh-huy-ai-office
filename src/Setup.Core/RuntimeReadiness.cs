using System.Text.Json;

namespace Setup.Core;

public static class RuntimeReadiness
{
    private static readonly HashSet<string> Required = new(StringComparer.Ordinal)
    {
        "sql", "identity-db", "identity", "rabbitmq", "redis", "jaeger", "otel-collector",
        "core-api", "agent-worker", "web", "bootstrap"
    };

    public static bool IsReady(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return false;
        var states = new Dictionary<string, (string State, string Health, int ExitCode)>(StringComparer.Ordinal);
        try
        {
            if (output.TrimStart().StartsWith('['))
            {
                using var document = JsonDocument.Parse(output);
                foreach (var entry in document.RootElement.EnumerateArray()) Add(entry, states);
            }
            else
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    using var document = JsonDocument.Parse(line);
                    Add(document.RootElement, states);
                }
            return Required.All(service => states.TryGetValue(service, out var current) &&
                (service == "bootstrap" ? current.State == "exited" && current.ExitCode == 0 :
                    current.State == "running" && current.Health is "" or "healthy"));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    private static void Add(JsonElement entry, Dictionary<string, (string State, string Health, int ExitCode)> states)
    {
        var name = entry.GetProperty("Service").GetString()!;
        var state = entry.GetProperty("State").GetString()!;
        var health = entry.TryGetProperty("Health", out var value) ? value.GetString() ?? "" : "";
        var exit = entry.GetProperty("ExitCode").GetInt32();
        if (!states.TryAdd(name, (state, health, exit))) throw new InvalidOperationException("Duplicate service.");
    }
}
