namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class TaskStatusRealtimeIsolationTests
{
    [Xunit.Fact]
    public void CompanyGroup_IsDeterministicForSameAuthorityScope()
    {
        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var companyId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var first = global::MinhHuy.AIOffice.Core.Api.Realtime.TaskStatusRealtime.CompanyGroup(tenantId, companyId);
        var second = global::MinhHuy.AIOffice.Core.Api.Realtime.TaskStatusRealtime.CompanyGroup(tenantId, companyId);

        Xunit.Assert.Equal(first, second);
        Xunit.Assert.Equal($"tenant:{tenantId:D}:company:{companyId:D}", first);
    }

    [Xunit.Fact]
    public void CompanyGroup_IsolatesCompaniesWithinSameTenant()
    {
        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var companyA = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var companyB = Guid.Parse("33333333-3333-3333-3333-333333333333");

        var groupA = global::MinhHuy.AIOffice.Core.Api.Realtime.TaskStatusRealtime.CompanyGroup(tenantId, companyA);
        var groupB = global::MinhHuy.AIOffice.Core.Api.Realtime.TaskStatusRealtime.CompanyGroup(tenantId, companyB);

        Xunit.Assert.NotEqual(groupA, groupB);
    }

    [Xunit.Fact]
    public void CompanyGroup_IsolatesSameCompanyIdentifierAcrossTenants()
    {
        var tenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var tenantB = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var companyId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var groupA = global::MinhHuy.AIOffice.Core.Api.Realtime.TaskStatusRealtime.CompanyGroup(tenantA, companyId);
        var groupB = global::MinhHuy.AIOffice.Core.Api.Realtime.TaskStatusRealtime.CompanyGroup(tenantB, companyId);

        Xunit.Assert.NotEqual(groupA, groupB);
    }

    [Xunit.Theory]
    [Xunit.InlineData("00000000-0000-0000-0000-000000000000", "22222222-2222-2222-2222-222222222222")]
    [Xunit.InlineData("11111111-1111-1111-1111-111111111111", "00000000-0000-0000-0000-000000000000")]
    public void CompanyGroup_FailsClosedWhenAuthorityScopeIsIncomplete(string tenant, string company)
    {
        Xunit.Assert.Throws<ArgumentException>(() =>
            global::MinhHuy.AIOffice.Core.Api.Realtime.TaskStatusRealtime.CompanyGroup(Guid.Parse(tenant), Guid.Parse(company)));
    }

    [Xunit.Fact]
    public void Publisher_FailsClosedBeforeDispatchWhenTaskIdentityIsMissing()
    {
        var publisher = new global::MinhHuy.AIOffice.Core.Api.Realtime.SignalRTaskStatusPublisher(null!);
        var message = new global::MinhHuy.AIOffice.Core.Api.Realtime.TaskStatusChanged(Guid.Empty, "running", DateTimeOffset.UtcNow);

        Xunit.Assert.Throws<ArgumentException>(() =>
            publisher.PublishTaskStatusAsync(Guid.NewGuid(), Guid.NewGuid(), message));
    }

    [Xunit.Fact]
    public void Publisher_FailsClosedBeforeDispatchWhenStepIdentityIsMissing()
    {
        var publisher = new global::MinhHuy.AIOffice.Core.Api.Realtime.SignalRTaskStatusPublisher(null!);
        var message = new global::MinhHuy.AIOffice.Core.Api.Realtime.TaskStepStatusChanged(Guid.NewGuid(), Guid.Empty, "running", DateTimeOffset.UtcNow);

        Xunit.Assert.Throws<ArgumentException>(() =>
            publisher.PublishStepStatusAsync(Guid.NewGuid(), Guid.NewGuid(), message));
    }

    [Xunit.Theory]
    [Xunit.InlineData("")]
    [Xunit.InlineData("   ")]
    public void Publisher_FailsClosedBeforeDispatchWhenWorkerIdentityIsMissing(string workerId)
    {
        var publisher = new global::MinhHuy.AIOffice.Core.Api.Realtime.SignalRTaskStatusPublisher(null!);
        var message = new global::MinhHuy.AIOffice.Core.Api.Realtime.WorkerStatusChanged(Guid.NewGuid(), workerId, "running", DateTimeOffset.UtcNow);

        Xunit.Assert.Throws<ArgumentException>(() =>
            publisher.PublishWorkerStatusAsync(Guid.NewGuid(), Guid.NewGuid(), message));
    }

    [Xunit.Theory]
    [Xunit.InlineData("", "approval_required")]
    [Xunit.InlineData("approval", "")]
    [Xunit.InlineData("   ", "approval_required")]
    [Xunit.InlineData("approval", "   ")]
    public void Publisher_FailsClosedBeforeDispatchWhenAttentionMetadataIsIncomplete(string kind, string reasonCode)
    {
        var publisher = new global::MinhHuy.AIOffice.Core.Api.Realtime.SignalRTaskStatusPublisher(null!);
        var message = new global::MinhHuy.AIOffice.Core.Api.Realtime.TaskAttentionRequired(Guid.NewGuid(), kind, reasonCode, DateTimeOffset.UtcNow);

        Xunit.Assert.Throws<ArgumentException>(() =>
            publisher.PublishAttentionRequiredAsync(Guid.NewGuid(), Guid.NewGuid(), message));
    }
}
