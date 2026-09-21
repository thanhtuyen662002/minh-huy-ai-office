namespace MinhHuy.AIOffice.Shared.Contracts;

public sealed record ErpEvaluationGateDecision(
    string CandidateId,
    string CandidateVersion,
    int EvaluatedCases,
    int PassedCases,
    bool CanRelease,
    string Reason);

public static class ErpEvaluationGate
{
    public static ErpEvaluationGateDecision Decide(
        IReadOnlyCollection<ErpEvaluationCase> cases,
        IReadOnlyCollection<ErpEvaluationObservation> observations,
        string candidateId,
        string candidateVersion)
    {
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentNullException.ThrowIfNull(observations);
        Require(candidateId, nameof(candidateId));
        Require(candidateVersion, nameof(candidateVersion));

        if (cases.Count == 0)
        {
            return new(candidateId, candidateVersion, 0, 0, false, "no_cases");
        }

        var candidateObservations = observations
            .Where(x => string.Equals(x.CandidateId, candidateId, StringComparison.Ordinal) &&
                        string.Equals(x.CandidateVersion, candidateVersion, StringComparison.Ordinal))
            .ToArray();

        var results = ErpEvaluationHarness.Evaluate(cases, candidateObservations);
        var expectedCaseIds = cases.Select(x => x.CaseId).ToHashSet(StringComparer.Ordinal);
        var observedCaseIds = candidateObservations.Select(x => x.CaseId).ToHashSet(StringComparer.Ordinal);

        if (!expectedCaseIds.SetEquals(observedCaseIds))
        {
            return new(candidateId, candidateVersion, results.Count, results.Count(x => x.Passed), false, "incomplete_evidence");
        }

        var passed = results.Count(x => x.Passed);
        var canRelease = passed == cases.Count;
        return new(
            candidateId,
            candidateVersion,
            results.Count,
            passed,
            canRelease,
            canRelease ? "all_cases_passed" : "evaluation_failed");
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException($"{name} must be a non-empty canonical value.", name);
        }
    }
}
