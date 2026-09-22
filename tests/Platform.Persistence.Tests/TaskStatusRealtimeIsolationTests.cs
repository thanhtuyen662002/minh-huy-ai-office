using Xunit;
using MinhHuy.AIOffice.Core.Api.Realtime;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class TaskStatusRealtimeIsolationTests
{
    [Fact]
    public void CompanyGroup_IsDeterministicForSameAuthorityScope()
    {
        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var companyId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var first = TaskStatusRealtime.CompanyGroup(tenantId, companyId);
        var second = TaskStatusRealtime.CompanyGroup(tenantId, companyId);

        Assert.Equal(first, second);
        Assert.Equal($"tenant:{tenantId:D}:company:{companyId:D}", first);
    }

    [Fact]
    public void CompanyGroup_IsolatesCompaniesWithinSameTenant()
    {
        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var companyA = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var companyB = Guid.Parse("33333333-3333-3333-3333-333333333333");

        var groupA = TaskStatusRealtime.CompanyGroup(tenantId, companyA);
        var groupB = TaskStatusRealtime.CompanyGroup(tenantId, companyB);

        Assert.NotEqual(groupA, groupB);
    }

    [Fact]
    public void CompanyGroup_IsolatesSameCompanyIdentifierAcrossTenants()
    {
        var tenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var tenantB = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var companyId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var groupA = TaskStatusRealtime.CompanyGroup(tenantA, companyId);
        var groupB = TaskStatusRealtime.CompanyGroup(tenantB, companyId);

        Assert.NotEqual(groupA, groupB);
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000", "22222222-2222-2222-2222-222222222222")]
    [InlineData("11111111-1111-1111-1111-111111111111", "00000000-0000-0000-0000-000000000000")]
    public void CompanyGroup_FailsClosedWhenAuthorityScopeIsIncomplete(string tenant, string company)
    {
        Assert.Throws<ArgumentException>(() =>
            TaskStatusRealtime.CompanyGroup(Guid.Parse(tenant), Guid.Parse(company)));
    }

    [Fact]
    public void Publisher_FailsClosedBeforeDispatchWhenTaskIdentityIsMissing()
    {
        var publisher = new SignalRTaskStatusPublisher(null!);
        var message = new TaskStatusChanged(Guid.Empty, "running", DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentException>(() =>
            publisher.PublishTaskStatusAsync(Guid.NewGuid(), Guid.NewGuid(), message));
    }

    [Fact]
    public void Publisher_FailsClosedBeforeDispatchWhenStepIdentityIsMissing()
    {
        var publisher = new SignalRTaskStatusPublisher(null!);
        var message = new TaskStepStatusChanged(Guid.NewGuid(), Guid.Empty, "running", DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentException>(() =>
            publisher.PublishStepStatusAsync(Guid.NewGuid(), Guid.NewGuid(), message));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Publisher_FailsClosedBeforeDispatchWhenWorkerIdentityIsMissing(string workerId)
    {
        var publisher = new SignalRTaskStatusPublisher(null!);
        var message = new WorkerStatusChanged(Guid.NewGuid(), workerId, "running", DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentException>(() =>
            publisher.PublishWorkerStatusAsync(Guid.NewGuid(), Guid.NewGuid(), message));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Publisher_FailsClosedBeforeDispatchWhenTaskStatusIsMissing(string status)
    {
        var publisher = new SignalRTaskStatusPublisher(null!);
        var message = new TaskStatusChanged(Guid.NewGuid(), status, DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentException>(() =>
            publisher.PublishTaskStatusAsync(Guid.NewGuid(), Guid.NewGuid(), message));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Publisher_FailsClosedBeforeDispatchWhenStepStatusIsMissing(string status)
    {
        var publisher = new SignalRTaskStatusPublisher(null!);
        var message = new TaskStepStatusChanged(Guid.NewGuid(), Guid.NewGuid(), status, DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentException>(() =>
            publisher.PublishStepStatusAsync(Guid.NewGuid(), Guid.NewGuid(), message));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Publisher_FailsClosedBeforeDispatchWhenWorkerStatusIsMissing(string status)
    {
        var publisher = new SignalRTaskStatusPublisher(null!);
        var message = new WorkerStatusChanged(Guid.NewGuid(), "worker-1", status, DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentException>(() =>
            publisher.PublishWorkerStatusAsync(Guid.NewGuid(), Guid.NewGuid(), message));
    }

    [Theory]
    [InlineData("", "approval_required")]
    [InlineData("approval", "")]
    [InlineData("   ", "approval_required")]
    [InlineData("approval", "   ")]
    public void Publisher_FailsClosedBeforeDispatchWhenAttentionMetadataIsIncomplete(string kind, string reasonCode)
    {
        var publisher = new SignalRTaskStatusPublisher(null!);
        var message = new TaskAttentionRequired(Guid.NewGuid(), kind, reasonCode, DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentException>(() =>
            publisher.PublishAttentionRequiredAsync(Guid.NewGuid(), Guid.NewGuid(), message));
    }
}
