using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Shared.Contracts.Tests;

public sealed class CustomerBillingTests
{
    [Fact]
    public void DemandAuthority_AcceptsExactServerScope()
    {
        var authority = new CompanyBillingAuthority(Guid.NewGuid(), Guid.NewGuid());
        var plan = new CompanyPlan(authority, "internal", 1_000_000, 250_000);

        plan.DemandAuthority(authority);

        Assert.Equal(750_000L, plan.RemainingAiCredits);
    }

    [Fact]
    public void DemandAuthority_RejectsCrossCompanyScope()
    {
        var tenantId = Guid.NewGuid();
        var plan = new CompanyPlan(
            new CompanyBillingAuthority(tenantId, Guid.NewGuid()),
            "internal",
            1_000_000,
            0);

        Assert.Throws<UnauthorizedAccessException>(() =>
            plan.DemandAuthority(new CompanyBillingAuthority(tenantId, Guid.NewGuid())));
    }

    [Fact]
    public void DemandAuthority_RejectsCrossTenantScope()
    {
        var companyId = Guid.NewGuid();
        var plan = new CompanyPlan(
            new CompanyBillingAuthority(Guid.NewGuid(), companyId),
            "internal",
            1_000_000,
            0);

        Assert.Throws<UnauthorizedAccessException>(() =>
            plan.DemandAuthority(new CompanyBillingAuthority(Guid.NewGuid(), companyId)));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Authority_FailsClosedWhenScopeIsMissing(bool missingTenant, bool missingCompany)
    {
        var authority = new CompanyBillingAuthority(
            missingTenant ? Guid.Empty : Guid.NewGuid(),
            missingCompany ? Guid.Empty : Guid.NewGuid());

        Assert.Throws<ArgumentException>(authority.Validate);
    }

    [Fact]
    public void Validate_RejectsNegativeCreditCounters()
    {
        var authority = new CompanyBillingAuthority(Guid.NewGuid(), Guid.NewGuid());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CompanyPlan(authority, "internal", -1, 0).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CompanyPlan(authority, "internal", 1, -1).Validate());
    }

    [Fact]
    public void RemainingCredits_NeverBecomesNegative()
    {
        var authority = new CompanyBillingAuthority(Guid.NewGuid(), Guid.NewGuid());
        var plan = new CompanyPlan(authority, "internal", 10, 12);

        plan.Validate();

        Assert.Equal(0L, plan.RemainingAiCredits);
    }
}
