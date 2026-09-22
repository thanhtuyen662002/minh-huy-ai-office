extern alias RuntimeWorker;

using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts;
using Platform.Persistence;
using RuntimeWorker::MinhHuy.AIOffice.Agent.Worker;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class AuthorizedWorkStepExecutorTests
{
    [Fact]
    public async Task Allowed_execution_uses_durable_user_authority_audits_then_invokes_raw_executor()
    {
        await using var db = CreateDb();
        var fixture = await SeedTaskAsync(db);
        var sink = new RecordingSink();
        var raw = new RecordingExecutor(sink);
        var executor = CreateExecutor(db, raw, sink, ToolRiskLevel.High);

        var result = await executor.ExecuteAsync(fixture.Envelope, fixture.Lease, CancellationToken.None);

        Assert.Equal(WorkDeliveryOutcome.Completed, result.Outcome);
        Assert.True(raw.Executed);
        var audit = Assert.Single(sink.Entries);
        Assert.True(audit.Authorized);
        Assert.Equal(fixture.UserId, audit.UserId);
        Assert.Equal(fixture.Envelope.TenantId, audit.TenantId);
        Assert.Equal(fixture.Envelope.CompanyId, audit.CompanyId);
        Assert.Equal(fixture.Envelope.TaskId, audit.TaskId);
        Assert.Equal(fixture.Envelope.MessageId.ToString("N"), audit.ExecutionId);
    }

    [Fact]
    public async Task Denied_execution_audits_and_never_invokes_raw_executor()
    {
        await using var db = CreateDb();
        var fixture = await SeedTaskAsync(db);
        var sink = new RecordingSink();
        var raw = new RecordingExecutor(sink);
        var executor = CreateExecutor(db, raw, sink, ToolRiskLevel.Low);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => executor.ExecuteAsync(fixture.Envelope, fixture.Lease, CancellationToken.None));

        Assert.False(raw.Executed);
        var audit = Assert.Single(sink.Entries);
        Assert.False(audit.Authorized);
        Assert.Equal("risk_exceeds_permission", audit.DecisionReason);
        Assert.Equal(fixture.UserId, audit.UserId);
    }

    private static AuthorizedWorkStepExecutor CreateExecutor(PlatformDbContext db, RecordingExecutor raw, RecordingSink sink, ToolRiskLevel permissionRisk)
    {
        var metadata = new StaticMetadataProvider(new TrustedToolExecutionMetadata("ledger", "post", ToolRiskLevel.Medium));
        var permissions = new StaticPermissionProvider(permissionRisk);
        var factory = new TrustedToolAuthorizationRequestFactory(db);
        var gate = new AuthorizedToolExecutionGate(new ToolAuthorizationPolicy(), new ToolExecutionAuditService(sink));
        return new AuthorizedWorkStepExecutor(raw, metadata, permissions, factory, gate);
    }

    private static PlatformDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>().UseInMemoryDatabase($"authorized-worker-{Guid.NewGuid():N}").Options;
        return new PlatformDbContext(options);
    }

    private static async Task<Fixture> SeedTaskAsync(PlatformDbContext db)
    {
        var tenantId = Guid.NewGuid(); var companyId = Guid.NewGuid(); var userId = Guid.NewGuid(); var taskId = Guid.NewGuid(); var stepId = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        db.Tasks.Add(new TaskRecord { TenantId = tenantId, CompanyId = companyId, Id = taskId, CreatedByUserId = userId, CreatedAtUtc = now, UpdatedAtUtc = now });
        await db.SaveChangesAsync();
        return new Fixture(userId, WorkDispatchEnvelope.Create(Guid.NewGuid(), tenantId, companyId, taskId, stepId, 1, null, now), new WorkLeaseSnapshot(Guid.NewGuid(), "test-worker", 1, now.AddMinutes(5)));
    }

    private sealed record Fixture(Guid UserId, WorkDispatchEnvelope Envelope, WorkLeaseSnapshot Lease);

    private sealed class StaticMetadataProvider(TrustedToolExecutionMetadata metadata) : ITrustedToolExecutionMetadataProvider
    {
        public Task<TrustedToolExecutionMetadata> GetAsync(WorkDispatchEnvelope envelope, WorkLeaseSnapshot lease, CancellationToken cancellationToken) => Task.FromResult(metadata);
    }

    private sealed class StaticPermissionProvider(ToolRiskLevel risk) : IToolPermissionProvider
    {
        public Task<IReadOnlyCollection<ToolPermission>> GetAsync(ToolAuthorizationRequest request, CancellationToken cancellationToken)
        {
            IReadOnlyCollection<ToolPermission> permissions = [new ToolPermission(request.TenantId, request.CompanyId, request.UserId, request.Resource, request.Action, risk)];
            return Task.FromResult(permissions);
        }
    }

    private sealed class RecordingExecutor(RecordingSink sink) : IRawWorkStepExecutor
    {
        public bool Executed { get; private set; }
        public Task<WorkStepExecutionResult> ExecuteAsync(WorkDispatchEnvelope envelope, WorkLeaseSnapshot lease, CancellationToken cancellationToken)
        {
            Assert.True(Assert.Single(sink.Entries).Authorized);
            Executed = true;
            return Task.FromResult(new WorkStepExecutionResult(WorkDeliveryOutcome.Completed, null, 1));
        }
    }

    private sealed class RecordingSink : IToolExecutionAuditSink
    {
        public List<ToolExecutionAuditEntry> Entries { get; } = [];
        public Task AppendAsync(ToolExecutionAuditEntry entry, CancellationToken cancellationToken = default) { Entries.Add(entry); return Task.CompletedTask; }
    }
}
