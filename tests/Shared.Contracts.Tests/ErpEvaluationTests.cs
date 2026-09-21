using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Shared.Contracts.Tests;

public sealed class ErpEvaluationTests
{
    [Fact]
    public void Evaluate_IsDeterministicAcrossInputOrdering()
    {
        var cases = new[]
        {
            Case("b", "expected-b"),
            Case("a", "expected-a")
        };
        var observations = new[]
        {
            Observation("b", "expected-b", "model-z", "2"),
            Observation("a", "expected-a", "model-b", "1"),
            Observation("a", "expected-a", "model-a", "2")
        };

        var forward = ErpEvaluationHarness.Evaluate(cases, observations);
        var reverse = ErpEvaluationHarness.Evaluate(cases.Reverse().ToArray(), observations.Reverse().ToArray());

        Assert.Equal(forward, reverse);
        Assert.All(forward, x => Assert.True(x.Passed));
        Assert.Equal(new[] { "model-a", "model-b", "model-z" }, forward.Select(x => x.CandidateId));
    }

    [Theory]
    [InlineData("tenant-other", "company-a", "scope_mismatch")]
    [InlineData("tenant-a", "company-other", "scope_mismatch")]
    public void Evaluate_FailsClosedAcrossTenantOrCompany(string tenantId, string companyId, string reason)
    {
        var result = ErpEvaluationHarness.Evaluate(
            new[] { Case("case-1", "expected") },
            new[] { Observation("case-1", "expected", tenantId: tenantId, companyId: companyId) });

        Assert.False(result.Single().Passed);
        Assert.Equal(reason, result.Single().Reason);
    }

    [Fact]
    public void Evaluate_DistinguishesInputDriftFromOutputRegression()
    {
        var testCase = Case("case-1", "expected");

        var inputDrift = ErpEvaluationHarness.Evaluate(
            new[] { testCase },
            new[] { Observation("case-1", "expected") with { InputHash = "different-input" } }).Single();
        var regression = ErpEvaluationHarness.Evaluate(
            new[] { testCase },
            new[] { Observation("case-1", "different-output") }).Single();

        Assert.Equal("input_mismatch", inputDrift.Reason);
        Assert.Equal("output_mismatch", regression.Reason);
    }

    [Fact]
    public void Evaluate_RejectsDuplicateCandidateEvidence()
    {
        var observation = Observation("case-1", "expected");

        Assert.Throws<InvalidOperationException>(() => ErpEvaluationHarness.Evaluate(
            new[] { Case("case-1", "expected") },
            new[] { observation, observation }));
    }

    [Fact]
    public void Evaluate_RejectsUnknownCaseEvidence()
    {
        Assert.Throws<InvalidOperationException>(() => ErpEvaluationHarness.Evaluate(
            new[] { Case("case-1", "expected") },
            new[] { Observation("case-other", "expected") }));
    }

    [Fact]
    public void Evaluate_RejectsNonCanonicalAuthority()
    {
        Assert.Throws<ArgumentException>(() => ErpEvaluationHarness.Evaluate(
            new[] { Case("case-1", "expected") with { CompanyId = " company-a " } },
            Array.Empty<ErpEvaluationObservation>()));
    }

    private static ErpEvaluationCase Case(string id, string expected) =>
        new(id, "tenant-a", "company-a", $"input-{id}", expected, "dataset-v1");

    private static ErpEvaluationObservation Observation(
        string id,
        string output,
        string candidate = "model-a",
        string version = "1",
        string tenantId = "tenant-a",
        string companyId = "company-a") =>
        new(id, tenantId, companyId, $"input-{id}", output, candidate, version);
}
