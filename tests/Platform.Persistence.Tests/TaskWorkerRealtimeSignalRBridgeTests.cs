using MinhHuy.AIOffice.Core.Api.Realtime;
using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace Platform.Persistence.Tests;

public sealed class TaskWorkerRealtimeSignalRBridgeTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CompanyId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TaskId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset OccurredAt = DateTimeOffset.Parse("2026-09-22T06:00:00+00:00");

    [Fact]
    public async Task PublishAsync_MapsTaskStatusWithAuthoritativeScopeAndCancellation()
    {
        var publisher = new RecordingPublisher();
        var bridge = new TaskWorkerRealtimeSignalRBridge(publisher);
        using var source = new CancellationTokenSource();
        var realtimeEvent = TaskWorkerRealtimeEvent.Create(
            Guid.NewGuid(), TaskWorkerEventKind.TaskStatusChanged, TenantId, CompanyId, TaskId,
            null, null, TaskExecutionStatus.Running, null, null, 1, OccurredAt);

        await bridge.PublishAsync(realtimeEvent, source.Token);

        Assert.Equal(TenantId, publisher.TenantId);
        Assert.Equal(CompanyId, publisher.CompanyId);
        Assert.Equal(TaskId, publisher.TaskStatus?.TaskId);
        Assert.Equal(TaskExecutionStatus.Running.ToString(), publisher.TaskStatus?.Status);
        Assert.Equal(OccurredAt, publisher.TaskStatus?.OccurredAtUtc);
        Assert.Equal(source.Token, publisher.CancellationToken);
    }

    [Theory]
    [InlineData(TaskWorkerEventKind.ApprovalRequired, "approval", "permission-required")]
    [InlineData(TaskWorkerEventKind.Blocked, "blocked", "task-blocked")]
    public async Task PublishAsync_MapsAttentionWithoutSecretBearingPayload(
        TaskWorkerEventKind kind,
        string expectedKind,
        string expectedReason)
    {
        var publisher = new RecordingPublisher();
        var bridge = new TaskWorkerRealtimeSignalRBridge(publisher);
        var taskStatus = kind == TaskWorkerEventKind.ApprovalRequired
            ? TaskExecutionStatus.PermissionRequired
            : TaskExecutionStatus.Blocked;
        var realtimeEvent = TaskWorkerRealtimeEvent.Create(
            Guid.NewGuid(), kind, TenantId, CompanyId, TaskId,
            null, null, taskStatus, null, null, 1, OccurredAt);

        await bridge.PublishAsync(realtimeEvent);

        Assert.Equal(TenantId, publisher.TenantId);
        Assert.Equal(CompanyId, publisher.CompanyId);
        Assert.Equal(TaskId, publisher.Attention?.TaskId);
        Assert.Equal(expectedKind, publisher.Attention?.Kind);
        Assert.Equal(expectedReason, publisher.Attention?.ReasonCode);
        Assert.Equal(OccurredAt, publisher.Attention?.OccurredAtUtc);
    }

    [Fact]
    public async Task PublishAsync_RejectsNullEventBeforePublisherDispatch()
    {
        var publisher = new RecordingPublisher();
        var bridge = new TaskWorkerRealtimeSignalRBridge(publisher);

        await Assert.ThrowsAsync<ArgumentNullException>(() => bridge.PublishAsync(null!));

        Assert.Equal(0, publisher.DispatchCount);
    }

    private sealed class RecordingPublisher : ITaskStatusPublisher
    {
        public Guid TenantId { get; private set; }
        public Guid CompanyId { get; private set; }
        public TaskStatusChanged? TaskStatus { get; private set; }
        public TaskAttentionRequired? Attention { get; private set; }
        public CancellationToken CancellationToken { get; private set; }
        public int DispatchCount { get; private set; }

        public Task PublishTaskStatusAsync(Guid tenantId, Guid companyId, TaskStatusChanged message, CancellationToken cancellationToken = default)
        {
            Record(tenantId, companyId, cancellationToken);
            TaskStatus = message;
            return Task.CompletedTask;
        }

        public Task PublishStepStatusAsync(Guid tenantId, Guid companyId, TaskStepStatusChanged message, CancellationToken cancellationToken = default)
        {
            Record(tenantId, companyId, cancellationToken);
            return Task.CompletedTask;
        }

        public Task PublishWorkerStatusAsync(Guid tenantId, Guid companyId, WorkerStatusChanged message, CancellationToken cancellationToken = default)
        {
            Record(tenantId, companyId, cancellationToken);
            return Task.CompletedTask;
        }

        public Task PublishAttentionRequiredAsync(Guid tenantId, Guid companyId, TaskAttentionRequired message, CancellationToken cancellationToken = default)
        {
            Record(tenantId, companyId, cancellationToken);
            Attention = message;
            return Task.CompletedTask;
        }

        private void Record(Guid tenantId, Guid companyId, CancellationToken cancellationToken)
        {
            TenantId = tenantId;
            CompanyId = companyId;
            CancellationToken = cancellationToken;
            DispatchCount++;
        }
    }
}
