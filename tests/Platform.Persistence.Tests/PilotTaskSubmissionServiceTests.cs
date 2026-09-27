using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class PilotTaskSubmissionServiceTests
{
    [Fact]
    public async Task SubmitAsync_PersistsAuthorizedTaskGraphAndPendingOutboxDispatch()
    {
        await using var context = CreateContext();
        var seeded = await SeedIdentityAsync(context);
        var service = CreateService(context);
        var request = new PilotTaskSubmissionRequest(
            "pilot-request-1",
            seeded.DataSourceId,
            "  read the current customer balance  ".Trim(),
            MaxAttempts: 4);

        var result = await service.SubmitAsync(seeded.Authority, request);
        var task = await context.Tasks.SingleAsync();
        var step = await context.TaskSteps.SingleAsync();
        var execution = await context.TaskStepExecutions.SingleAsync();
        var dispatch = await context.TaskDispatches.SingleAsync();
        var taskEvent = await context.TaskEvents.SingleAsync();

        Assert.Equal(task.Id, result.TaskId);
        Assert.Equal(step.Id, result.StepId);
        Assert.Equal(TaskExecutionStatus.Pending, result.TaskStatus);
        Assert.Equal(WorkDispatchState.Pending, result.DispatchState);
        Assert.Equal(seeded.Authority.TenantId, task.TenantId);
        Assert.Equal(seeded.Authority.CompanyId, task.CompanyId);
        Assert.Equal(seeded.Authority.UserId, task.CreatedByUserId);
        Assert.Equal(PilotTaskIdentity.StepKey, step.StepKey);
        Assert.Equal(1, step.Attempt);
        Assert.Equal(1, execution.Attempt);
        Assert.Equal(dispatch.MessageId, PilotTaskIdentity.ForMessage(task.Id, step.Id));
        Assert.Equal(WorkDispatchState.Pending, dispatch.State);
        Assert.Equal(1, dispatch.Attempt);
        Assert.Equal(1, taskEvent.Sequence);
        Assert.Equal(PilotTaskRequestEvent.EventType, taskEvent.EventType);
        Assert.DoesNotContain("secretref", taskEvent.PayloadJson, StringComparison.OrdinalIgnoreCase);

        var persistedEvent = JsonSerializer.Deserialize<PilotTaskRequestEvent>(
            taskEvent.PayloadJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(persistedEvent);
        Assert.Equal(request.IdempotencyKey, persistedEvent!.IdempotencyKey);
        Assert.Equal(request.DataSourceId, persistedEvent.DataSourceId);
        Assert.Equal(request.Question, persistedEvent.Question);
        Assert.Equal(request.MaxAttempts, persistedEvent.MaxAttempts);
    }

    [Fact]
    public async Task SubmitAsync_CustomerContractReturnsStableMessageIdentityAndStatus()
    {
        await using var context = CreateContext();
        var seeded = await SeedIdentityAsync(context);
        var service = CreateService(context);

        var result = await service.SubmitAsync(
            seeded.Authority,
            new CustomerPilotTaskRequest(seeded.DataSourceId, "read the current customer balance"),
            "pilot-customer-request");

        Assert.Equal(TaskExecutionStatus.Pending, result.Status);
        Assert.Equal(PilotTaskIdentity.ForMessage(result.TaskId, result.StepId), result.MessageId);
        Assert.Equal("pilot-customer-request", result.IdempotencyKey);
        Assert.Equal(WorkDispatchState.Pending, result.DispatchState);
    }

    [Fact]
    public async Task SubmitAsync_ExactReplayReturnsTheOriginalDurableIdentity()
    {
        await using var context = CreateContext();
        var seeded = await SeedIdentityAsync(context);
        var service = CreateService(context);
        var request = new PilotTaskSubmissionRequest(
            "pilot-replay-1",
            seeded.DataSourceId,
            "read the current customer balance");

        var first = await service.SubmitAsync(seeded.Authority, request);
        seeded.DataSource.IsEnabled = false;
        await context.SaveChangesAsync();

        // An exact retry is a read of the already accepted request. It must not require the
        // source to remain enabled after acceptance, and it must not create another graph.
        var replay = await service.SubmitAsync(seeded.Authority, request);

        Assert.Equal(first, replay);
        Assert.Equal(1, await context.Tasks.CountAsync());
        Assert.Equal(1, await context.TaskSteps.CountAsync());
        Assert.Equal(1, await context.TaskStepExecutions.CountAsync());
        Assert.Equal(1, await context.TaskDispatches.CountAsync());
        Assert.Equal(1, await context.TaskEvents.CountAsync());
    }

    [Fact]
    public async Task SubmitAsync_ConflictingReplayFailsClosedWithoutChangingDurableEvidence()
    {
        await using var context = CreateContext();
        var seeded = await SeedIdentityAsync(context);
        var service = CreateService(context);
        var request = new PilotTaskSubmissionRequest(
            "pilot-conflict-1",
            seeded.DataSourceId,
            "read the current customer balance");
        await service.SubmitAsync(seeded.Authority, request);

        var conflicting = request with { Question = "write a new customer balance" };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SubmitAsync(seeded.Authority, conflicting));

        var persistedEvent = await context.TaskEvents.SingleAsync();
        var payload = JsonSerializer.Deserialize<PilotTaskRequestEvent>(
            persistedEvent.PayloadJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(payload);
        Assert.Equal(request.Question, payload!.Question);
        Assert.Equal(1, await context.Tasks.CountAsync());
        Assert.Equal(1, await context.TaskDispatches.CountAsync());
    }

    [Fact]
    public async Task SubmitAsync_CrossCompanyDataSourceFailsClosedAndWritesNothing()
    {
        await using var context = CreateContext();
        var first = await SeedIdentityAsync(context);
        var second = await SeedIdentityAsync(context);
        var service = CreateService(context);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.SubmitAsync(
                first.Authority,
                new PilotTaskSubmissionRequest(
                    "pilot-cross-company-1",
                    second.DataSourceId,
                    "read the current customer balance")));

        Assert.Empty(context.Tasks);
        Assert.Empty(context.TaskSteps);
        Assert.Empty(context.TaskStepExecutions);
        Assert.Empty(context.TaskDispatches);
        Assert.Empty(context.TaskEvents);
    }

    [Fact]
    public async Task SubmitAsync_DisabledOrWriteOnlyDataSourceFailsClosed()
    {
        await using var context = CreateContext();
        var seeded = await SeedIdentityAsync(context);
        var service = CreateService(context);

        seeded.DataSource.AllowRead = false;
        await context.SaveChangesAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.SubmitAsync(
                seeded.Authority,
                new PilotTaskSubmissionRequest(
                    "pilot-disabled-1",
                    seeded.DataSourceId,
                    "read the current customer balance")));

        Assert.Empty(context.Tasks);
        Assert.Empty(context.TaskDispatches);
    }

    [Fact]
    public async Task ResultService_ReturnsLatestDurableCheckpointWithinOwnerScope()
    {
        await using var context = CreateContext();
        var seeded = await SeedIdentityAsync(context);
        var submissionService = CreateService(context);
        var accepted = await submissionService.SubmitAsync(
            seeded.Authority,
            new PilotTaskSubmissionRequest(
                "pilot-result-1",
                seeded.DataSourceId,
                "read the current customer balance"));

        var task = await context.Tasks.SingleAsync();
        var step = await context.TaskSteps.SingleAsync();
        var execution = await context.TaskStepExecutions.SingleAsync();
        var dispatch = await context.TaskDispatches.SingleAsync();
        var now = DateTimeOffset.UtcNow;
        task.Status = TaskExecutionStatus.Completed;
        task.UpdatedAtUtc = now;
        step.Status = TaskStepStatus.Completed;
        step.UpdatedAtUtc = now;
        dispatch.State = WorkDispatchState.Acknowledged;
        execution.UpdatedAtUtc = now;
        context.TaskCheckpoints.Add(new TaskCheckpointRecord
        {
            TenantId = task.TenantId,
            CompanyId = task.CompanyId,
            TaskId = task.Id,
            StepId = step.Id,
            Version = 1,
            PayloadJson = "{\"answer\":\"42\"}",
            CreatedAtUtc = now
        });
        await context.SaveChangesAsync();

        var result = await new PilotTaskResultService(
                context,
                new EfAuthorizationDirectory(context))
            .GetAsync(seeded.Authority, accepted.TaskId);

        Assert.NotNull(result);
        Assert.Equal(accepted.TaskId, result!.TaskId);
        Assert.Equal(TaskExecutionStatus.Completed, result.TaskStatus);
        Assert.Equal(TaskStepStatus.Completed, result.StepStatus);
        Assert.Equal(WorkDispatchState.Acknowledged, result.DispatchState);
        Assert.Equal(1, result.Attempt);
        Assert.Equal("{\"answer\":\"42\"}", result.ResultPayloadJson);
        Assert.True(result.UpdatedAtUtc >= now);
    }

    [Fact]
    public async Task ResultService_DoesNotReturnAnotherUserTask()
    {
        await using var context = CreateContext();
        var seeded = await SeedIdentityAsync(context);
        var otherUserId = Guid.NewGuid();
        context.Users.Add(new PlatformUserRecord
        {
            TenantId = seeded.Authority.TenantId,
            Id = otherUserId,
            IdentityProvider = "test",
            Subject = $"user-{otherUserId:N}",
            DisplayName = "Other User",
            IsActive = true
        });
        context.CompanyMemberships.Add(new CompanyMembershipRecord
        {
            TenantId = seeded.Authority.TenantId,
            CompanyId = seeded.Authority.CompanyId,
            UserId = otherUserId,
            IsActive = true
        });
        await context.SaveChangesAsync();

        var accepted = await CreateService(context).SubmitAsync(
            seeded.Authority,
            new PilotTaskSubmissionRequest(
                "pilot-owner-1",
                seeded.DataSourceId,
                "read the current customer balance"));

        var otherAuthority = AuthorizationContext.Create(
            seeded.Authority.TenantId,
            seeded.Authority.CompanyId,
            otherUserId);
        var result = await new PilotTaskResultService(
                context,
                new EfAuthorizationDirectory(context))
            .GetAsync(otherAuthority, accepted.TaskId);

        Assert.Null(result);
    }

    private static PilotTaskSubmissionService CreateService(PlatformDbContext context) =>
        new(context, new EfAuthorizationDirectory(context));

    private static PlatformDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase($"pilot-task-{Guid.NewGuid():N}")
            .Options;
        return new PlatformDbContext(options);
    }

    private static async Task<SeededIdentity> SeedIdentityAsync(PlatformDbContext context)
    {
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var dataSourceId = Guid.NewGuid();
        context.Users.Add(new PlatformUserRecord
        {
            TenantId = tenantId,
            Id = userId,
            IdentityProvider = "test",
            Subject = $"user-{userId:N}",
            DisplayName = "Pilot User",
            IsActive = true
        });
        context.Companies.Add(new CompanyRecord
        {
            TenantId = tenantId,
            Id = companyId,
            Code = $"COMPANY-{companyId:N}",
            Name = "Pilot Company",
            IsActive = true
        });
        context.CompanyMemberships.Add(new CompanyMembershipRecord
        {
            TenantId = tenantId,
            CompanyId = companyId,
            UserId = userId,
            IsActive = true
        });
        var dataSource = new DataSourceRecord
        {
            TenantId = tenantId,
            CompanyId = companyId,
            Id = dataSourceId,
            LogicalName = $"pilot-source-{dataSourceId:N}",
            Kind = "erp",
            Environment = "test",
            Purpose = "pilot read-only source",
            ConnectionSecretReference = "secretref://env/PILOT_ERP_CONNECTION",
            AllowRead = true,
            AllowWrite = false,
            MaxConcurrency = 1,
            IsEnabled = true,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        context.DataSources.Add(dataSource);
        await context.SaveChangesAsync();

        return new(
            AuthorizationContext.Create(tenantId, companyId, userId),
            dataSourceId,
            dataSource);
    }

    private sealed record SeededIdentity(
        AuthorizationContext Authority,
        Guid DataSourceId,
        DataSourceRecord DataSource);
}
