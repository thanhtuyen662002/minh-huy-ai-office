namespace MinhHuy.AIOffice.Shared.Contracts.Development;

public sealed record ItAgentChangeProposal(
    string TenantId,
    string CompanyId,
    string IssueId,
    string Branch,
    string CandidateId,
    string CandidateVersion,
    string SourceCommit,
    string ChangeImpactHash);

public sealed record ItAgentVerificationEvidence(
    string CandidateId,
    string CandidateVersion,
    string SourceCommit,
    bool CiPassed,
    bool EvaluationPassed,
    string CiEvidenceHash,
    string EvaluationEvidenceHash);

public sealed record ItAgentDevelopmentDecision(
    string IssueId,
    string SourceCommit,
    bool CanAdvanceToRelease,
    string Reason);

public static class ItAgentDevelopmentGate
{
    public static ItAgentDevelopmentDecision Decide(
        ItAgentChangeProposal proposal,
        ItAgentVerificationEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(evidence);
        Validate(proposal);
        Validate(evidence);

        if (!StringComparer.Ordinal.Equals(proposal.CandidateId, evidence.CandidateId) ||
            !StringComparer.Ordinal.Equals(proposal.CandidateVersion, evidence.CandidateVersion) ||
            !StringComparer.Ordinal.Equals(proposal.SourceCommit, evidence.SourceCommit))
        {
            return Deny(proposal, "evidence_identity_mismatch");
        }

        if (!evidence.CiPassed)
        {
            return Deny(proposal, "ci_not_passed");
        }

        if (!evidence.EvaluationPassed)
        {
            return Deny(proposal, "evaluation_not_passed");
        }

        return new(proposal.IssueId, proposal.SourceCommit, true, "verified_candidate");
    }

    private static ItAgentDevelopmentDecision Deny(ItAgentChangeProposal proposal, string reason) =>
        new(proposal.IssueId, proposal.SourceCommit, false, reason);

    private static void Validate(ItAgentChangeProposal proposal)
    {
        Require(proposal.TenantId, nameof(proposal.TenantId));
        Require(proposal.CompanyId, nameof(proposal.CompanyId));
        Require(proposal.IssueId, nameof(proposal.IssueId));
        Require(proposal.Branch, nameof(proposal.Branch));
        Require(proposal.CandidateId, nameof(proposal.CandidateId));
        Require(proposal.CandidateVersion, nameof(proposal.CandidateVersion));
        Require(proposal.SourceCommit, nameof(proposal.SourceCommit));
        Require(proposal.ChangeImpactHash, nameof(proposal.ChangeImpactHash));
    }

    private static void Validate(ItAgentVerificationEvidence evidence)
    {
        Require(evidence.CandidateId, nameof(evidence.CandidateId));
        Require(evidence.CandidateVersion, nameof(evidence.CandidateVersion));
        Require(evidence.SourceCommit, nameof(evidence.SourceCommit));
        Require(evidence.CiEvidenceHash, nameof(evidence.CiEvidenceHash));
        Require(evidence.EvaluationEvidenceHash, nameof(evidence.EvaluationEvidenceHash));
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !StringComparer.Ordinal.Equals(value, value.Trim()))
        {
            throw new ArgumentException($"{name} must be non-empty canonical text.", name);
        }
    }
}
