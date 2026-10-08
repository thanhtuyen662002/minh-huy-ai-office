extern alias RuntimeWorker;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts;
using MinhHuyAiOffice.Shared.Contracts;
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
    [Theory]
    [InlineData(false, "grant")]
    [InlineData(true, "grant")]
    [InlineData(false, "membership")]
    [InlineData(true, "membership")]
    [InlineData(false, "owner")]
    [InlineData(true, "owner")]
    public async Task Queued_revocation_is_durably_failed_before_dead_letter_and_repeated_delivery_never_reexecutes(bool ai, string revocation)
    {
        await using var db = CreateContext();
        var (identity, envelope) = await SubmitAndPublishAsync(db);
        await RevokeAsync(db, revocation);
        var pipeline = new RecordingPipeline(db, ai);
        var result = await pipeline.Handler.HandleAsync(envelope, CancellationToken.None);
        Assert.Equal(WorkFailureClass.Authorization, result.FailureClass);
        Assert.Equal(BrokerSettlement.DeadLetter, WorkDeliverySettlement.Resolve(result.Outcome, result.FailureClass,
            envelope.Attempt, result.MaxAttempts));
        Assert.Equal(TaskExecutionStatus.Failed, (await db.Tasks.SingleAsync()).Status);
        Assert.Equal(TaskStepStatus.Failed, (await db.TaskSteps.SingleAsync()).Status);
        var dispatch = await db.TaskDispatches.SingleAsync();
        Assert.Equal(WorkDispatchState.DeadLettered, dispatch.State);
        Assert.Null(dispatch.AcknowledgedAtUtc);
        Assert.NotNull(dispatch.DeadLetteredAtUtc);
        Assert.Null((await db.TaskStepExecutions.SingleAsync()).LeaseId);
        Assert.Empty(db.TaskCheckpoints);
        var eventCount = await db.TaskEvents.CountAsync();
        var denied = Assert.Single(pipeline.Audit.Entries);
        Assert.False(denied.Authorized);
        Assert.Equal(identity.Authority.UserId, denied.UserId);
        for (var repeat = 0; repeat < 2; repeat++)
        {
            var repeated = await pipeline.Handler.HandleAsync(envelope, CancellationToken.None);
            Assert.Equal(WorkFailureClass.Authorization, repeated.FailureClass);
        }
        Assert.Equal(eventCount, await db.TaskEvents.CountAsync());
        Assert.Single(pipeline.Audit.Entries);
        pipeline.AssertNoUse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transient_retry_keeps_its_budget_but_revocation_fences_retry_and_original_redelivery(bool ai)
    {
        await using var db = CreateContext();
        var (_, original) = await SubmitAndPublishAsync(db);
        var pipeline = new RecordingPipeline(db, ai, failProbe: true);
        var transient = await pipeline.Handler.HandleAsync(original, CancellationToken.None);
        Assert.Equal(WorkFailureClass.Transient, transient.FailureClass);
        Assert.Equal(3, transient.MaxAttempts);
        Assert.Equal(BrokerSettlement.Requeue, WorkDeliverySettlement.Resolve(transient.Outcome, transient.FailureClass,
            original.Attempt, transient.MaxAttempts));
        var retry = Assert.IsType<WorkDispatchEnvelope>(transient.DurableRetryEnvelope);
        Assert.Equal(2, retry.Attempt);
        Assert.Equal(TaskExecutionStatus.Running, (await db.Tasks.SingleAsync()).Status);
        Assert.Equal(1, pipeline.Resolver.Calls);
        Assert.Single(pipeline.Probe.Connections);
        await RevokeAsync(db, "grant");
        var denied = await pipeline.Handler.HandleAsync(retry, CancellationToken.None);
        Assert.Equal(WorkFailureClass.Authorization, denied.FailureClass);
        Assert.Equal(TaskExecutionStatus.Failed, (await db.Tasks.SingleAsync()).Status);
        foreach (var delivery in new[] { retry, original, original, retry })
        {
            var repeated = await pipeline.Handler.HandleAsync(delivery, CancellationToken.None);
            Assert.Equal(BrokerSettlement.DeadLetter, WorkDeliverySettlement.Resolve(repeated.Outcome,
                repeated.FailureClass, delivery.Attempt, repeated.MaxAttempts));
        }
        Assert.All(await db.TaskDispatches.ToArrayAsync(), row => Assert.Equal(WorkDispatchState.DeadLettered, row.State));
        Assert.Equal(1, pipeline.Resolver.Calls);
        Assert.Single(pipeline.Probe.Connections);
        Assert.Equal(0, pipeline.Evidence.Calls);
        Assert.Equal(0, pipeline.Model.Calls);
        Assert.Empty(db.TaskCheckpoints);
        Assert.Equal(2, pipeline.Audit.Entries.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Terminal_persistence_failure_never_returns_settlement_and_recovers_after_existing_lease_expiry(bool ai)
    {
        var failure = new FailTerminalSave();
        var options = new DbContextOptionsBuilder<PlatformDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(failure).Options;
        WorkDispatchEnvelope envelope;
        await using (var db = new PlatformDbContext(options))
        {
            (_, envelope) = await SubmitAndPublishAsync(db);
            await RevokeAsync(db, "grant");
            var pipeline = new RecordingPipeline(db, ai);
            failure.Enabled = true;
            await Assert.ThrowsAsync<DbUpdateException>(() => pipeline.Handler.HandleAsync(envelope, CancellationToken.None));
            // Reusing this delivery scope must not return a false durable denial
            // from the uncommitted tracked projection after SaveChanges failed.
            Assert.Equal(TaskExecutionStatus.Running, (await db.Tasks.SingleAsync()).Status);
            Assert.Equal(WorkDispatchState.Published, (await db.TaskDispatches.SingleAsync()).State);
            await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.Handler.HandleAsync(envelope, CancellationToken.None));
            pipeline.AssertNoUse();
        }
        failure.Enabled = false;
        await using var recovered = new PlatformDbContext(options);
        var dispatch = await recovered.TaskDispatches.SingleAsync();
        Assert.Equal(WorkDispatchState.Published, dispatch.State);
        Assert.Null(dispatch.DeadLetteredAtUtc);
        Assert.Null(dispatch.AcknowledgedAtUtc);
        Assert.Equal(TaskExecutionStatus.Running, (await recovered.Tasks.SingleAsync()).Status);
        var execution = await recovered.TaskStepExecutions.SingleAsync();
        Assert.Null(execution.LastFailureClass);
        // The established bounded lease still applies to infrastructure recovery.
        // Advance only this synthetic lease instead of sleeping or weakening fencing.
        execution.LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
        await recovered.SaveChangesAsync();
        var retryPipeline = new RecordingPipeline(recovered, ai);
        var denied = await retryPipeline.Handler.HandleAsync(envelope, CancellationToken.None);
        Assert.Equal(WorkFailureClass.Authorization, denied.FailureClass);
        Assert.Equal(TaskExecutionStatus.Failed, (await recovered.Tasks.SingleAsync()).Status);
        Assert.Equal(WorkDispatchState.DeadLettered, dispatch.State);
        retryPipeline.AssertNoUse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Audit_infrastructure_failure_and_cancellation_are_not_relabelled_as_authorization_denial(bool ai)
    {
        await using var db = CreateContext();
        var (_, envelope) = await SubmitAndPublishAsync(db);
        await RevokeAsync(db, "grant");
        var pipeline = new RecordingPipeline(db, ai);
        pipeline.Audit.Fault = new DbUpdateException("synthetic audit storage unavailable");
        await Assert.ThrowsAsync<DbUpdateException>(() => pipeline.Handler.HandleAsync(envelope, CancellationToken.None));
        Assert.Equal(WorkDispatchState.Published, (await db.TaskDispatches.SingleAsync()).State);
        Assert.Null((await db.TaskStepExecutions.SingleAsync()).LastFailureClass);
        pipeline.AssertNoUse();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.Handler.HandleAsync(envelope, cancelled.Token));
        Assert.Null((await db.TaskStepExecutions.SingleAsync()).LastFailureClass);
        pipeline.AssertNoUse();
    }

    private static async Task<(SeededIdentity, WorkDispatchEnvelope)> SubmitAndPublishAsync(PlatformDbContext db)
    {
        var identity = await SeedIdentityAsync(db);
        await new PilotTaskSubmissionService(db, new EfAuthorizationDirectory(db)).SubmitAsync(identity.Authority,
            new CustomerPilotTaskRequest(identity.DataSourceId, "synthetic queue fixture"), "queue-fixture");
        var dispatch = await db.TaskDispatches.SingleAsync();
        WorkerExecutionStateMachine.TransitionDispatch(dispatch, WorkDispatchState.Published, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        return (identity, WorkDispatchEnvelope.Create(dispatch.MessageId, dispatch.TenantId, dispatch.CompanyId,
            dispatch.TaskId, dispatch.StepId, dispatch.Attempt, dispatch.CheckpointVersion, dispatch.PublishedAtUtc!.Value));
    }

    private static async Task RevokeAsync(PlatformDbContext db, string revocation)
    {
        if (revocation == "grant") (await db.DataSourceSecretBindings.SingleAsync()).IsEnabled = false;
        if (revocation == "membership") (await db.CompanyMemberships.SingleAsync()).IsActive = false;
        if (revocation == "owner") (await db.Users.SingleAsync()).IsActive = false;
        await db.SaveChangesAsync();
    }

    private sealed class RecordingPipeline
    {
        public FixedSecretResolver Resolver { get; } = new("synthetic-only-no-secret");
        public RecordingProbe Probe { get; }
        public RecordingEvidence Evidence { get; } = new();
        public RecordingModel Model { get; } = new();
        public RecordingAuditSink Audit { get; } = new();
        public PersistentWorkDeliveryHandler Handler { get; }
        public RecordingPipeline(PlatformDbContext db, bool ai, bool failProbe = false)
        {
            Probe = new(failProbe);
            var secrets = new CompositeSecretResolver([Resolver]);
            IRawWorkStepExecutor raw = ai ? new PilotAiQuestionExecutor(db, secrets, Probe, Evidence, Model,
                new("fixture", "fixture")) : new PilotDataSourceProbeExecutor(db, secrets, Probe);
            ITrustedToolExecutionMetadataProvider metadata = ai ? new PilotAiQuestionToolMetadataProvider(db) : new PilotDataSourceToolMetadataProvider(db);
            IToolPermissionProvider permissions = ai ? new PilotAiQuestionToolPermissionProvider(db) : new PilotDataSourceToolPermissionProvider(db);
            Handler = new(db, new AuthorizedWorkStepExecutor(raw, metadata, permissions,
                new TrustedToolAuthorizationRequestFactory(db), new AuthorizedToolExecutionGate(
                    new ToolAuthorizationPolicy(), new ToolExecutionAuditService(Audit))));
        }
        public void AssertNoUse()
        {
            Assert.Equal(0, Resolver.Calls);
            Assert.Empty(Probe.Connections);
            Assert.Equal(0, Evidence.Calls);
            Assert.Equal(0, Model.Calls);
        }
    }

    private sealed class RecordingEvidence : IPilotErpEvidenceReader
    {
        public int Calls { get; private set; }
        public ValueTask<PilotErpEvidence> ReadAsync(string connectionString, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Unexpected evidence use in denied fixture");
        }
    }

    private sealed class RecordingModel : IAiGateway
    {
        public int Calls { get; private set; }
        public Task<AiGatewayResponse> ExecuteAsync(AiGatewayRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Unexpected model use in denied fixture");
        }
    }

    private sealed class FailTerminalSave : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<TaskRecord>()
                .Any(entry => entry.Entity.Status == TaskExecutionStatus.Failed))
                throw new DbUpdateException("synthetic terminal persistence failure");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
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
        BindingFixture.Grant(context, tenantId, companyId, "secretref://env/PILOT_ERP_CONNECTION");
        await context.SaveChangesAsync();
        return new(AuthorizationContext.Create(tenantId, companyId, userId), dataSourceId);
    }

    private sealed record SeededIdentity(AuthorizationContext Authority, Guid DataSourceId);

    private sealed class FixedSecretResolver(string value) : ISecretResolver
    {
        public string Provider => "env";
        public int Calls { get; private set; }

        public ValueTask<string> ResolveAsync(
            SecretReference reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult(value);
        }
    }

    private sealed class RecordingProbe(bool fail = false) : IDataSourceConnectionProbe
    {
        public List<string> Connections { get; } = [];
        public ValueTask ProbeAsync(string connectionString, bool requireReadOnly, CancellationToken cancellationToken = default)
        {
            Assert.True(requireReadOnly);
            return ProbeAsync(connectionString, cancellationToken);
        }

        public ValueTask ProbeAsync(
            string connectionString,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Connections.Add(connectionString);
            if (fail) throw new InvalidOperationException("synthetic probe infrastructure fault");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingAuditSink : IToolExecutionAuditSink
    {
        public List<ToolExecutionAuditEntry> Entries { get; } = [];
        public Exception? Fault { get; set; }

        public Task AppendAsync(
            ToolExecutionAuditEntry entry,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Fault is not null) throw Fault;
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }
}
