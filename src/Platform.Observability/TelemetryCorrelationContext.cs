using System.Diagnostics;

namespace MinhHuy.AIOffice.Platform.Observability;

public sealed record TelemetryCorrelationContext(
    string? TaskId,
    string? CompanyId,
    string TraceId)
{
    public const int MaximumIdentifierLength = 128;

    public static TelemetryCorrelationContext Create(
        string? taskId,
        string? companyId,
        Activity? activity = null)
    {
        if (!TryCreate(taskId, companyId, activity, out var context))
        {
            throw new FormatException(
                "Telemetry correlation identifiers must be at most 128 characters and contain only letters, digits, '.', '_', ':', or '-'.");
        }

        return context;
    }

    public static bool TryCreate(
        string? taskId,
        string? companyId,
        Activity? activity,
        out TelemetryCorrelationContext context)
    {
        context = default!;

        if (!TryNormalize(taskId, out var normalizedTaskId) ||
            !TryNormalize(companyId, out var normalizedCompanyId))
        {
            return false;
        }

        context = new TelemetryCorrelationContext(
            normalizedTaskId,
            normalizedCompanyId,
            activity?.TraceId.ToString() ?? string.Empty);

        return true;
    }

    public void ApplyTo(Activity? activity)
    {
        if (activity is null)
        {
            return;
        }

        if (TaskId is not null)
        {
            activity.SetTag(TelemetryDimensions.TaskId, TaskId);
        }

        if (CompanyId is not null)
        {
            activity.SetTag(TelemetryDimensions.CompanyId, CompanyId);
        }

        activity.SetTag(TelemetryDimensions.TraceId, activity.TraceId.ToString());
    }

    public IReadOnlyDictionary<string, object?> ToLogScope()
    {
        return new Dictionary<string, object?>
        {
            ["TaskId"] = TaskId,
            ["CompanyId"] = CompanyId,
            ["TraceId"] = TraceId
        };
    }

    private static bool TryNormalize(string? value, out string? normalized)
    {
        normalized = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var candidate = value.Trim();
        if (candidate.Length > MaximumIdentifierLength)
        {
            return false;
        }

        if (!candidate.All(character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '_' or ':' or '-'))
        {
            return false;
        }

        normalized = candidate;
        return true;
    }
}
