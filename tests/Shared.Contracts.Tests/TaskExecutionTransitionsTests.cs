using Xunit;

namespace MinhHuy.AIOffice.Shared.Contracts.Tests;

public sealed class TaskExecutionTransitionsTests
{
    [Theory]
    [InlineData(TaskExecutionStatus.Running, TaskExecutionStatus.ClarificationRequired)]
    [InlineData(TaskExecutionStatus.Running, TaskExecutionStatus.PermissionRequired)]
    [InlineData(TaskExecutionStatus.Running, TaskExecutionStatus.Blocked)]
    [InlineData(TaskExecutionStatus.Running, TaskExecutionStatus.Failed)]
    [InlineData(TaskExecutionStatus.Running, TaskExecutionStatus.Completed)]
    [InlineData(TaskExecutionStatus.ClarificationRequired, TaskExecutionStatus.Running)]
    [InlineData(TaskExecutionStatus.PermissionRequired, TaskExecutionStatus.Running)]
    [InlineData(TaskExecutionStatus.Blocked, TaskExecutionStatus.Running)]
    public void CanTransition_AllowsResumableRuntimeStates(
        TaskExecutionStatus current,
        TaskExecutionStatus next)
    {
        Assert.True(TaskExecutionTransitions.CanTransition(current, next));
    }

    [Theory]
    [InlineData(TaskExecutionStatus.Completed, TaskExecutionStatus.Running)]
    [InlineData(TaskExecutionStatus.Failed, TaskExecutionStatus.Running)]
    [InlineData(TaskExecutionStatus.Completed, TaskExecutionStatus.Failed)]
    public void CanTransition_RejectsLeavingTerminalStates(
        TaskExecutionStatus current,
        TaskExecutionStatus next)
    {
        Assert.False(TaskExecutionTransitions.CanTransition(current, next));
    }
}
