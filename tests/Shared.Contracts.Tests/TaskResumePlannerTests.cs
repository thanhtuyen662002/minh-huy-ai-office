using Xunit;

namespace MinhHuy.AIOffice.Shared.Contracts.Tests;

public sealed class TaskResumePlannerTests
{
    [Fact]
    public void GetReadyStepIds_ReturnsOnlyPendingStepsWhoseDependenciesCompleted()
    {
        var completed = Guid.NewGuid();
        var ready = Guid.NewGuid();
        var waiting = Guid.NewGuid();
        var result = TaskResumePlanner.GetReadyStepIds(
            TaskExecutionStatus.Running,
            [new(completed, TaskStepStatus.Completed), new(ready, TaskStepStatus.Pending), new(waiting, TaskStepStatus.Pending)],
            [new(ready, completed), new(waiting, ready)]);
        Assert.Equal([ready], result);
    }

    [Theory]
    [InlineData(TaskExecutionStatus.ClarificationRequired)]
    [InlineData(TaskExecutionStatus.PermissionRequired)]
    [InlineData(TaskExecutionStatus.Blocked)]
    [InlineData(TaskExecutionStatus.Failed)]
    [InlineData(TaskExecutionStatus.Completed)]
    public void GetReadyStepIds_DoesNotScheduleSuspendedOrTerminalTasks(TaskExecutionStatus status)
    {
        var id = Guid.NewGuid();
        Assert.Empty(TaskResumePlanner.GetReadyStepIds(status, [new(id, TaskStepStatus.Pending)], []));
    }

    [Fact]
    public void GetReadyStepIds_DoesNotRescheduleRunningStepsAfterResume()
    {
        var id = Guid.NewGuid();
        Assert.Empty(TaskResumePlanner.GetReadyStepIds(TaskExecutionStatus.Running, [new(id, TaskStepStatus.Running)], []));
    }

    [Fact]
    public void GetReadyStepIds_RejectsDependencyOnUnknownStep()
    {
        var id = Guid.NewGuid();
        Assert.Throws<InvalidOperationException>(() => TaskResumePlanner.GetReadyStepIds(
            TaskExecutionStatus.Running,
            [new(id, TaskStepStatus.Pending)],
            [new(id, Guid.NewGuid())]));
    }

    [Fact]
    public void GetReadyStepIds_RejectsDependencyCycles()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        Assert.Throws<InvalidOperationException>(() => TaskResumePlanner.GetReadyStepIds(
            TaskExecutionStatus.Running,
            [new(a, TaskStepStatus.Pending), new(b, TaskStepStatus.Pending)],
            [new(a, b), new(b, a)]));
    }
}
