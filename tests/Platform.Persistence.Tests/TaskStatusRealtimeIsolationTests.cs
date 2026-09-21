extern alias CoreApi;

using Xunit;
using Realtime = CoreApi::MinhHuy.AIOffice.Core.Api.Realtime;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class TaskStatusRealtimeIsolationTests
{
    [Fact]
    public void CompanyGroup_IsDeterministicForSameAuthorityScope()
    {
        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var companyId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var first = Realtime.TaskStatusRealtime.CompanyGroup(tenantId, companyId);
        var second = Realtime.TaskStatusRealtime.CompanyGroup(tenantId, companyId);

        Assert.Equal(first, second);
        Assert.Equal($"tenant:{tenantId:D}:company:{companyId:D}", first);
    }

    [Fact]
    public void CompanyGroup_IsolatesCompaniesWithinSameTenant()
    {
        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var companyA = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var companyB = Guid.Parse("33333333-3333-3333-3333-333333333333");

        var groupA = Realtime.TaskStatusRealtime.CompanyGroup(tenantId, companyA);
        var groupB = Realtime.TaskStatusRealtime.CompanyGroup(tenantId, companyB);

        Assert.NotEqual(groupA, groupB);
    }

    [Fact]
    public void CompanyGroup_IsolatesSameCompanyIdentifierAcrossTenants()
    {
        var tenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var tenantB = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var companyId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var groupA = Realtime.TaskStatusRealtime.CompanyGroup(tenantA, companyId);
        var groupB = Realtime.TaskStatusRealtime.CompanyGroup(tenantB, companyId);

        Assert.NotEqual(groupA, groupB);
    }
}
