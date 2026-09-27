extern alias RuntimeWorker;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts;
using Platform.Persistence;
using RuntimeWorker::MinhHuy.AIOffice.Agent.Worker;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

/// <summary>
/// Exercises the bounded pilot path after the API transaction and before a real RabbitMQ broker:
/// durable submission/outbox state is promoted to Published, the authorized worker executor
/// reads the event and data-source policy, and the delivery handler persists audit, checkpoint
/// and terminal task state before it returns an acknowledgement outcome.
/// </summary>
public sealed class PilotTaskPipelineIntegrationTests
{
    [Fact]
    public async Task Submitted_task_runs_through_authorized_probe_and_persists_terminal_evidence()
    {
        await using var db = CreateContext();
        var seeded = await SeedIdentityAsync(db);
        var submission = new PilotTaskSubmissionService(db, new EfAuthorizationDirectory(db));
        var accepted = await submission.SubmitAsync(
            seeded.Authority,
            new CustomerPilotTaskRequest(seeded.DataSourceId, "read the current customer balance"),
            "pilot-integration-1");

        var dispatch = await db.TaskDispatches.SingleAsync();
        WorkerExecutionStateMachine.TransitionDispatch(
            dispatch,
            WorkDispatchState.Published,
            DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        var envelope = WorkDispatchEnvelope.Create(
            dispatch.MessageId,
            dispatch.TenantId,
            dispatch.CompanyId,
            dispatch.TaskId,
            dispatch.StepId,
            dispatch.Attempt,
            dispatch.CheckpointVersion,
            dispatch.PublishedAtUtc!.Value);
        var probe = new RecordingProbe();
        var auditSink = new RecordingAuditSink();
        var rawExecutor = new PilotDataSourceProbeExecutor(
            db,
            new CompositeSecretResolver(new ISecretResolver[]
            {
                new FixedSecretResolver("synthetic-pilot-connection")
            }),
            probe);
        var authorizedExecutor = new AuthorizedWorkStepExecutor(
            rawExecutor,
            new PilotDataSourceToolMetadataProvider(db),
            new PilotDataSourceToolPermissionProvider(db),
            new TrustedToolAuthorizationRequestFactory(db),
            new AuthorizedToolExecutionGate(
                new ToolAuthorizationPolicy(),
                new ToolExecutionAuditService(auditSink)));
        var handler = new PersistentWorkDeliveryHandler(db, authorizedExecutor);

        var result = await handler.HandleAsync(envelope, CancellationToken.None);
        var task = await db.Tasks.SingleAsync();
        var step = await db.TaskSteps.SingleAsync();
        var checkpoint = await db.TaskCheckpoints.SingleAsync();

        Assert.Equal(WorkDeliveryOutcome.Completed, result.Outcome);
        Assert.Equal(accepted.TaskId, task.Id);
        Assert.Equal(TaskExecutionStatus.Completed, task.Status);
        Assert.Equal(TaskStepStatus.Completed, step.Status);
        Assert.Equal(WorkDispatchState.Acknowledged, dispatch.State);
        Assert.Equal(1, checkpoint.Version);
        Assert.Contains("read-only-connection-probe", checkpoint.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-pilot-connection", checkpoint.PayloadJson, StringComparison.Ordinal);
        Assert.Equal(["synthetic-pilot-connection"], probe.Connections);
        var audit = Assert.Single(auditSink.Entries);
        Assert.True(audit.Authorized);
        Assert.Equal(task.Id, audit.TaskId);
        Assert.Equal($"erp-data-source:{seeded.DataSourceId:N}", audit.Resource);
        Assert.Equal("connection-test", audit.Action);
        Assert.Equal(3, await db.TaskEvents.CountAsync());

        var statusEvents = await db.TaskEvents
            .Where(item => item.TaskId == task.Id && item.EventType.EndsWith("status.changed", StringComparison.Ordinal))
            .OrderBy(item => item.Sequence)
            .Select(item => item.PayloadJson)
            .ToArrayAsync();
        Assert.Equal(2, statusEvents.Length);
        Assert.Contains("Completed", string.Join('|', statusEvents), StringComparison.Ordinal);
    }

    private static PlatformDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase($"pilot-pipeline-{Guid.NewGuid():N}")
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
            Subject = $"pilot-{userId:N}",
            DisplayName = "Pilot User",
            IsActive = true
        });
        context.Companies.Add(new CompanyRecord
        {
            TenantId = tenantId,
            Id = companyId,
            Code = $"PILOT-{companyId:N}",
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
            LogicalName = "pilot-source",
            Kind = "sqlserver",
            Environment = "test",
            Purpose = "read-only pilot",
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
        return new(AuthorizationContext.Create(tenantId, companyId, userId), dataSourceId);
    }

    private sealed record SeededIdentity(AuthorizationContext Authority, Guid DataSourceId);

    private sealed class FixedSecretResolver(string value) : ISecretResolver
    {
        public string Provider => "env";

        public ValueTask<string> ResolveAsync(
            SecretReference reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(value);
        }
    }

    private sealed class RecordingProbe : IDataSourceConnectionProbe
    {
        public List<string> Connections { get; } = [];

        public ValueTask ProbeAsync(
            string connectionString,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Connections.Add(connectionString);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingAuditSink : IToolExecutionAuditSink
    {
        public List<ToolExecutionAuditEntry> Entries { get; } = [];

        public Task AppendAsync(
            ToolExecutionAuditEntry entry,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }
}
