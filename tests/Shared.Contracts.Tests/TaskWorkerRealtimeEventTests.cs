using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Shared.Contracts.Tests;

public sealed class TaskWorkerRealtimeEventTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid CompanyId = Guid.NewGuid();
    private static readonly Guid TaskId = Guid.NewGuid();

    [Fact]
    public void TaskStatusEvent_IsVersionedSequencedAndTenantScoped()
    {
        var evt = TaskWorkerRealtimeEvent.Create(Guid.NewGuid(), TaskWorkerEventKind.TaskStatusChanged,
            TenantId, CompanyId, TaskId, null, null, TaskExecutionStatus.Running, null, null, 7, DateTimeOffset.UtcNow);

        Assert.Equal(TaskWorkerRealtimeEvent.CurrentContractVersion, evt.ContractVersion);
        Assert.Equal(7, evt.Sequence);
        Assert.True(evt.IsInScope(TenantId, CompanyId));
        Assert.False(evt.IsInScope(TenantId, Guid.NewGuid()));
    }

    [Fact]
    public void StepStatusEvent_RequiresStepAndStepStatus()
    {
        Assert.Throws<ArgumentException>(() => TaskWorkerRealtimeEvent.Create(Guid.NewGuid(),
            TaskWorkerEventKind.StepStatusChanged, TenantId, CompanyId, TaskId, null, null,
            null, TaskStepStatus.Running, null, 1, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void WorkerStatusEvent_RequiresNonBlankWorkerIdentity()
    {
        Assert.Throws<ArgumentException>(() => TaskWorkerRealtimeEvent.Create(Guid.NewGuid(),
            TaskWorkerEventKind.WorkerStatusChanged, TenantId, CompanyId, TaskId, null, " ",
            null, null, WorkerExecutionStatus.Busy, 1, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ApprovalEvent_RequiresPermissionRequiredStatus()
    {
        Assert.Throws<ArgumentException>(() => TaskWorkerRealtimeEvent.Create(Guid.NewGuid(),
            TaskWorkerEventKind.ApprovalRequired, TenantId, CompanyId, TaskId, null, null,
            TaskExecutionStatus.Running, null, null, 1, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void EmptyAuthorityIdentifiersAndInvalidSequenceFailClosed()
    {
        Assert.Throws<ArgumentException>(() => TaskWorkerRealtimeEvent.Create(Guid.NewGuid(),
            TaskWorkerEventKind.TaskStatusChanged, Guid.Empty, CompanyId, TaskId, null, null,
            TaskExecutionStatus.Running, null, null, 1, DateTimeOffset.UtcNow));

        Assert.Throws<ArgumentOutOfRangeException>(() => TaskWorkerRealtimeEvent.Create(Guid.NewGuid(),
            TaskWorkerEventKind.TaskStatusChanged, TenantId, CompanyId, TaskId, null, null,
            TaskExecutionStatus.Running, null, null, 0, DateTimeOffset.UtcNow));
    }
}
