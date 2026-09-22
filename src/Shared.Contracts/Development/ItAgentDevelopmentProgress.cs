namespace MinhHuy.AIOffice.Shared.Contracts.Development;

public enum ItAgentDevelopmentStage
{
    IssueAccepted,
    BranchCreated,
    PullRequestOpened,
    VerificationPending,
    Verified
}

public sealed record ItAgentDevelopmentProgress(
    string TenantId,
    string CompanyId,
    string IssueId,
    ItAgentDevelopmentStage Stage,
    string? Branch,
    string? PullRequestId,
    string? SourceCommit)
{
    public ItAgentDevelopmentProgress AdvanceBranch(string branch)
    {
        AssertStage(ItAgentDevelopmentStage.IssueAccepted);
        Require(branch, nameof(branch));
        return this with { Stage = ItAgentDevelopmentStage.BranchCreated, Branch = branch };
    }

    public ItAgentDevelopmentProgress AdvancePullRequest(string pullRequestId, string sourceCommit)
    {
        AssertStage(ItAgentDevelopmentStage.BranchCreated);
        Require(Branch, nameof(Branch));
        Require(pullRequestId, nameof(pullRequestId));
        Require(sourceCommit, nameof(sourceCommit));
        return this with
        {
            Stage = ItAgentDevelopmentStage.PullRequestOpened,
            PullRequestId = pullRequestId,
            SourceCommit = sourceCommit
        };
    }

    public ItAgentDevelopmentProgress AwaitVerification()
    {
        AssertStage(ItAgentDevelopmentStage.PullRequestOpened);
        Require(PullRequestId, nameof(PullRequestId));
        Require(SourceCommit, nameof(SourceCommit));
        return this with { Stage = ItAgentDevelopmentStage.VerificationPending };
    }

    public ItAgentDevelopmentProgress ApplyVerification(ItAgentDevelopmentDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        AssertStage(ItAgentDevelopmentStage.VerificationPending);
        if (!StringComparer.Ordinal.Equals(IssueId, decision.IssueId) ||
            !StringComparer.Ordinal.Equals(SourceCommit, decision.SourceCommit))
        {
            throw new InvalidOperationException("Verification decision does not belong to this exact issue/source commit.");
        }

        if (!decision.CanAdvanceToRelease)
        {
            throw new InvalidOperationException($"Verification gate denied advancement: {decision.Reason}.");
        }

        return this with { Stage = ItAgentDevelopmentStage.Verified };
    }

    public static ItAgentDevelopmentProgress Start(string tenantId, string companyId, string issueId)
    {
        Require(tenantId, nameof(tenantId));
        Require(companyId, nameof(companyId));
        Require(issueId, nameof(issueId));
        return new(tenantId, companyId, issueId, ItAgentDevelopmentStage.IssueAccepted, null, null, null);
    }

    private void AssertStage(ItAgentDevelopmentStage expected)
    {
        if (Stage != expected)
        {
            throw new InvalidOperationException($"Development progression requires stage {expected}, current stage is {Stage}.");
        }
    }

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !StringComparer.Ordinal.Equals(value, value.Trim()))
        {
            throw new ArgumentException($"{name} must be non-empty canonical text.", name);
        }
    }
}
