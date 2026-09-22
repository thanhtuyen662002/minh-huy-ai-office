using MinhHuy.AIOffice.Shared.Contracts.Development;
using Xunit;

namespace MinhHuy.AIOffice.Shared.Contracts.Tests;

public sealed class ItAgentDevelopmentGateTests
{
    [Fact]
    public void Decide_AllowsOnlyExactCandidateWithCiAndEvaluationEvidence()
    {
        var decision = ItAgentDevelopmentGate.Decide(Proposal(), Evidence());

        Assert.True(decision.CanAdvanceToRelease);
        Assert.Equal("verified_candidate", decision.Reason);
        Assert.Equal("abc123", decision.SourceCommit);
    }

    [Theory]
    [InlineData("candidate-other", "v1", "abc123")]
    [InlineData("candidate", "v2", "abc123")]
    [InlineData("candidate", "v1", "def456")]
    public void Decide_FailsClosedWhenEvidenceIdentityDoesNotMatch(
        string candidateId,
        string candidateVersion,
        string sourceCommit)
    {
        var evidence = Evidence() with
        {
            CandidateId = candidateId,
            CandidateVersion = candidateVersion,
            SourceCommit = sourceCommit
        };

        var decision = ItAgentDevelopmentGate.Decide(Proposal(), evidence);

        Assert.False(decision.CanAdvanceToRelease);
        Assert.Equal("evidence_identity_mismatch", decision.Reason);
    }

    [Fact]
    public void Decide_FailsClosedWhenCiHasNotPassed()
    {
        var decision = ItAgentDevelopmentGate.Decide(Proposal(), Evidence() with { CiPassed = false });

        Assert.False(decision.CanAdvanceToRelease);
        Assert.Equal("ci_not_passed", decision.Reason);
    }

    [Fact]
    public void Decide_FailsClosedWhenEvaluationHasNotPassed()
    {
        var decision = ItAgentDevelopmentGate.Decide(Proposal(), Evidence() with { EvaluationPassed = false });

        Assert.False(decision.CanAdvanceToRelease);
        Assert.Equal("evaluation_not_passed", decision.Reason);
    }

    [Fact]
    public void Decide_RejectsNonCanonicalAuthorityAndEvidence()
    {
        Assert.Throws<ArgumentException>(() =>
            ItAgentDevelopmentGate.Decide(Proposal() with { CompanyId = " company" }, Evidence()));
        Assert.Throws<ArgumentException>(() =>
            ItAgentDevelopmentGate.Decide(Proposal(), Evidence() with { CiEvidenceHash = " " }));
    }

    private static ItAgentChangeProposal Proposal() =>
        new("tenant", "company", "31", "runtime/31-it-agent-development-loop", "candidate", "v1", "abc123", "impact-sha256");

    private static ItAgentVerificationEvidence Evidence() =>
        new("candidate", "v1", "abc123", true, true, "ci-sha256", "eval-sha256");
}
