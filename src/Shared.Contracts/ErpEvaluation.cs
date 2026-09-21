namespace MinhHuy.AIOffice.Shared.Contracts;

public sealed record ErpEvaluationCase(
    string CaseId,
    string TenantId,
    string CompanyId,
    string InputHash,
    string ExpectedOutputHash,
    string DatasetVersion);

public sealed record ErpEvaluationObservation(
    string CaseId,
    string TenantId,
    string CompanyId,
    string InputHash,
    string OutputHash,
    string CandidateId,
    string CandidateVersion);

public sealed record ErpEvaluationResult(
    string CaseId,
    string CandidateId,
    string CandidateVersion,
    bool Passed,
    string Reason);

public static class ErpEvaluationHarness
{
    public static IReadOnlyList<ErpEvaluationResult> Evaluate(
        IReadOnlyCollection<ErpEvaluationCase> cases,
        IReadOnlyCollection<ErpEvaluationObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentNullException.ThrowIfNull(observations);

        var caseIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var testCase in cases)
        {
            Validate(testCase);
            if (!caseIds.Add(testCase.CaseId))
            {
                throw new InvalidOperationException($"Duplicate evaluation case '{testCase.CaseId}'.");
            }
        }

        var observationKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var observation in observations)
        {
            Validate(observation);
            var key = $"{observation.CaseId}\u001f{observation.CandidateId}\u001f{observation.CandidateVersion}";
            if (!observationKeys.Add(key))
            {
                throw new InvalidOperationException("Duplicate candidate observation for an evaluation case.");
            }
        }

        var byCase = observations.ToLookup(x => x.CaseId, StringComparer.Ordinal);
        var results = new List<ErpEvaluationResult>();

        foreach (var testCase in cases.OrderBy(x => x.CaseId, StringComparer.Ordinal))
        {
            foreach (var observation in byCase[testCase.CaseId]
                         .OrderBy(x => x.CandidateId, StringComparer.Ordinal)
                         .ThenBy(x => x.CandidateVersion, StringComparer.Ordinal))
            {
                if (!string.Equals(testCase.TenantId, observation.TenantId, StringComparison.Ordinal) ||
                    !string.Equals(testCase.CompanyId, observation.CompanyId, StringComparison.Ordinal))
                {
                    results.Add(Fail(observation, "scope_mismatch"));
                    continue;
                }

                if (!string.Equals(testCase.InputHash, observation.InputHash, StringComparison.Ordinal))
                {
                    results.Add(Fail(observation, "input_mismatch"));
                    continue;
                }

                var passed = string.Equals(testCase.ExpectedOutputHash, observation.OutputHash, StringComparison.Ordinal);
                results.Add(new ErpEvaluationResult(
                    observation.CaseId,
                    observation.CandidateId,
                    observation.CandidateVersion,
                    passed,
                    passed ? "matched" : "output_mismatch"));
            }
        }

        var unknownCases = observations.Where(x => !caseIds.Contains(x.CaseId)).ToArray();
        if (unknownCases.Length != 0)
        {
            throw new InvalidOperationException("Observation references an unknown evaluation case.");
        }

        return results;
    }

    private static ErpEvaluationResult Fail(ErpEvaluationObservation observation, string reason) =>
        new(observation.CaseId, observation.CandidateId, observation.CandidateVersion, false, reason);

    private static void Validate(ErpEvaluationCase value)
    {
        Require(value.CaseId, nameof(value.CaseId));
        Require(value.TenantId, nameof(value.TenantId));
        Require(value.CompanyId, nameof(value.CompanyId));
        Require(value.InputHash, nameof(value.InputHash));
        Require(value.ExpectedOutputHash, nameof(value.ExpectedOutputHash));
        Require(value.DatasetVersion, nameof(value.DatasetVersion));
    }

    private static void Validate(ErpEvaluationObservation value)
    {
        Require(value.CaseId, nameof(value.CaseId));
        Require(value.TenantId, nameof(value.TenantId));
        Require(value.CompanyId, nameof(value.CompanyId));
        Require(value.InputHash, nameof(value.InputHash));
        Require(value.OutputHash, nameof(value.OutputHash));
        Require(value.CandidateId, nameof(value.CandidateId));
        Require(value.CandidateVersion, nameof(value.CandidateVersion));
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException($"{name} must be a non-empty canonical value.", name);
        }
    }
}
