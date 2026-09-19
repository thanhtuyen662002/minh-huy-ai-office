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
            [
                new(completed, TaskStepStatus.Completed),
                new(ready, TaskStepStatus.Pending),
                new(waiting, TaskStepStatus.Pending)
            ],
            [
                new(ready, completed),
                new(waiting, ready)
            ]);

        Assert.Equal([ready], result);
    }

    [Theory]
    [InlineData(TaskExecutionStatus.ClarificationRequired)]
    [InlineData(TaskExecutionStatus.PermissionRequired)]
    [InlineData(TaskExecutionStatus.Blocked)]
    [InlineData(TaskExecutionStatus.Failed)]
    [InlineData(TaskExecutionStatus.Completed)]
    public void GetReadyStepIds_DoesNotScheduleSuspendedOrTerminalTasks(TaskExecutionStatus taskStatus)
    {
        var stepId = Guid.NewGuid();

        var result = TaskResumePlanner.GetReadyStepIds(
            taskStatus,
            [new(stepId, TaskStepStatus.Pending)],
            []);

        Assert.Empty(result);
    }

    [Fact]
    public void GetReadyStepIds_DoesNotRescheduleRunningStepsAfterResume()
    {
        var running = Guid.NewGuid();

        var result = TaskResumePlanner.GetReadyStepIds(
            TaskExecutionStatus.Running,
            [new(running, TaskStepStatus.Running)],
            []);

        Assert.Empty(result);
    }

    [Fact]
    public void GetReadyStepIds_RejectsDependencyOnUnknownStep()
    {
        var stepId = Guid.NewGuid();

        Assert.Throws<InvalidOperationException>(() =>
            TaskResumePlanner.GetReadyStepIds(
                TaskExecutionStatus.Running,
                [new(stepId, TaskStepStatus.Pending)],
                [new(stepId, Guid.NewGuid())]));
    }

    [Fact]
    public void GetReadyStepIds_RejectsDependencyCycles()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        Assert.Throws<InvalidOperationException>(() =>
            TaskResumePlanner.GetReadyStepIds(
                TaskExecutionStatus.Running,
                [
                    new(first, TaskStepStatus.Pending),
                    new(second, TaskStepStatus.Pending)
                ],
                [
                    new(first, second),
                    new(second, first)
                ]));
    }
}
