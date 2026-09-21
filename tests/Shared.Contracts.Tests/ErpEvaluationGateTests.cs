using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Shared.Contracts.Tests;

public sealed class ErpEvaluationGateTests
{
    [Fact]
    public void Decide_AllowsReleaseOnlyWhenEveryCasePasses()
    {
        var cases = new[] { Case("one"), Case("two") };
        var observations = new[] { Observation("one"), Observation("two") };

        var decision = ErpEvaluationGate.Decide(cases, observations, "candidate", "v1");

        Assert.True(decision.CanRelease);
        Assert.Equal("all_cases_passed", decision.Reason);
        Assert.Equal(2, decision.EvaluatedCases);
        Assert.Equal(2, decision.PassedCases);
    }

    [Fact]
    public void Decide_FailsClosedWhenCandidateEvidenceIsIncomplete()
    {
        var decision = ErpEvaluationGate.Decide(
            new[] { Case("one"), Case("two") },
            new[] { Observation("one") },
            "candidate",
            "v1");

        Assert.False(decision.CanRelease);
        Assert.Equal("incomplete_evidence", decision.Reason);
    }

    [Fact]
    public void Decide_FailsClosedWhenAnyAccountingCaseRegresses()
    {
        var decision = ErpEvaluationGate.Decide(
            new[] { Case("one"), Case("two") },
            new[] { Observation("one"), Observation("two") with { OutputHash = "regressed" } },
            "candidate",
            "v1");

        Assert.False(decision.CanRelease);
        Assert.Equal("evaluation_failed", decision.Reason);
        Assert.Equal(1, decision.PassedCases);
    }

    [Fact]
    public void Decide_DoesNotBorrowEvidenceFromAnotherCandidate()
    {
        var observations = new[]
        {
            Observation("one"),
            Observation("two") with { CandidateId = "other" }
        };

        var decision = ErpEvaluationGate.Decide(
            new[] { Case("one"), Case("two") }, observations, "candidate", "v1");

        Assert.False(decision.CanRelease);
        Assert.Equal("incomplete_evidence", decision.Reason);
    }

    private static ErpEvaluationCase Case(string id) =>
        new(id, "tenant", "company", $"input-{id}", $"output-{id}", "accounting-fixture-v1");

    private static ErpEvaluationObservation Observation(string id) =>
        new(id, "tenant", "company", $"input-{id}", $"output-{id}", "candidate", "v1");
}
