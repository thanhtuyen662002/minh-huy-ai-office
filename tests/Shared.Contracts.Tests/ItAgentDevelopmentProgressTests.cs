using MinhHuy.AIOffice.Shared.Contracts.Development;
using Xunit;

namespace MinhHuy.AIOffice.Shared.Contracts.Tests;

public sealed class ItAgentDevelopmentProgressTests
{
    [Fact]
    public void Progression_RequiresOrderedIssueBranchPrVerificationFlow()
    {
        var progress = ItAgentDevelopmentProgress.Start("tenant", "company", "31")
            .AdvanceBranch("runtime/31")
            .AdvancePullRequest("86", "abc123")
            .AwaitVerification()
            .ApplyVerification(new("31", "abc123", true, "verified_candidate"));

        Assert.Equal(ItAgentDevelopmentStage.Verified, progress.Stage);
        Assert.Equal("runtime/31", progress.Branch);
        Assert.Equal("86", progress.PullRequestId);
        Assert.Equal("abc123", progress.SourceCommit);
    }

    [Fact]
    public void Progression_CannotSkipVersionedPullRequestGate()
    {
        var progress = ItAgentDevelopmentProgress.Start("tenant", "company", "31")
            .AdvanceBranch("runtime/31");

        Assert.Throws<InvalidOperationException>(() => progress.AwaitVerification());
    }

    [Fact]
    public void Progression_RejectsStaleVerificationForAnotherCommit()
    {
        var progress = ItAgentDevelopmentProgress.Start("tenant", "company", "31")
            .AdvanceBranch("runtime/31")
            .AdvancePullRequest("86", "abc123")
            .AwaitVerification();

        Assert.Throws<InvalidOperationException>(() =>
            progress.ApplyVerification(new("31", "stale", true, "verified_candidate")));
    }

    [Fact]
    public void Progression_RejectsDeniedVerification()
    {
        var progress = ItAgentDevelopmentProgress.Start("tenant", "company", "31")
            .AdvanceBranch("runtime/31")
            .AdvancePullRequest("86", "abc123")
            .AwaitVerification();

        Assert.Throws<InvalidOperationException>(() =>
            progress.ApplyVerification(new("31", "abc123", false, "ci_not_passed")));
    }

    [Fact]
    public void Start_RejectsNonCanonicalTenantCompanyOrIssueAuthority()
    {
        Assert.Throws<ArgumentException>(() => ItAgentDevelopmentProgress.Start(" tenant", "company", "31"));
        Assert.Throws<ArgumentException>(() => ItAgentDevelopmentProgress.Start("tenant", "company ", "31"));
        Assert.Throws<ArgumentException>(() => ItAgentDevelopmentProgress.Start("tenant", "company", " "));
    }
}
