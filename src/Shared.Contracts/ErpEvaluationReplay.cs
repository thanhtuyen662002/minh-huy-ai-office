using System.Text.Json;

namespace MinhHuy.AIOffice.Shared.Contracts;

public sealed record ErpEvaluationReplaySnapshot(
    string DatasetVersion,
    IReadOnlyList<ErpEvaluationCase> Cases);

public static class ErpEvaluationReplay
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(IReadOnlyCollection<ErpEvaluationCase> cases)
    {
        ArgumentNullException.ThrowIfNull(cases);

        if (cases.Count == 0)
        {
            throw new InvalidOperationException("Historical replay requires at least one evaluation case.");
        }

        var ordered = cases.OrderBy(x => x.CaseId, StringComparer.Ordinal).ToArray();
        _ = ErpEvaluationHarness.Evaluate(ordered, Array.Empty<ErpEvaluationObservation>());

        var datasetVersions = ordered.Select(x => x.DatasetVersion).Distinct(StringComparer.Ordinal).ToArray();
        if (datasetVersions.Length != 1)
        {
            throw new InvalidOperationException("Historical replay cannot mix dataset versions.");
        }

        return JsonSerializer.Serialize(new ErpEvaluationReplaySnapshot(datasetVersions[0], ordered), Options);
    }

    public static ErpEvaluationReplaySnapshot Deserialize(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new ArgumentException("Replay payload is required.", nameof(payload));
        }

        var snapshot = JsonSerializer.Deserialize<ErpEvaluationReplaySnapshot>(payload, Options)
            ?? throw new InvalidOperationException("Replay payload is invalid.");

        if (snapshot.Cases is null || snapshot.Cases.Count == 0)
        {
            throw new InvalidOperationException("Replay payload contains no cases.");
        }

        _ = ErpEvaluationHarness.Evaluate(snapshot.Cases, Array.Empty<ErpEvaluationObservation>());
        if (snapshot.Cases.Any(x => !string.Equals(x.DatasetVersion, snapshot.DatasetVersion, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Replay dataset version does not match case evidence.");
        }

        return snapshot with { Cases = snapshot.Cases.OrderBy(x => x.CaseId, StringComparer.Ordinal).ToArray() };
    }
}