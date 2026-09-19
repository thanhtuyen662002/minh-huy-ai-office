namespace MinhHuy.AIOffice.Shared.Contracts;

public enum TaskExecutionStatus
{
    Pending,
    Running,
    ClarificationRequired,
    PermissionRequired,
    Blocked,
    Failed,
    Completed
}

public enum TaskStepStatus
{
    Pending,
    Ready,
    Running,
    Blocked,
    Failed,
    Completed
}

public static class TaskExecutionTransitions
{
    public static bool CanTransition(TaskExecutionStatus current, TaskExecutionStatus next)
    {
        if (current == next)
        {
            return true;
        }

        return current switch
        {
            TaskExecutionStatus.Pending => next is TaskExecutionStatus.Running
                or TaskExecutionStatus.ClarificationRequired
                or TaskExecutionStatus.PermissionRequired
                or TaskExecutionStatus.Blocked
                or TaskExecutionStatus.Failed,
            TaskExecutionStatus.Running => next is TaskExecutionStatus.ClarificationRequired
                or TaskExecutionStatus.PermissionRequired
                or TaskExecutionStatus.Blocked
                or TaskExecutionStatus.Failed
                or TaskExecutionStatus.Completed,
            TaskExecutionStatus.ClarificationRequired => next is TaskExecutionStatus.Running
                or TaskExecutionStatus.Blocked
                or TaskExecutionStatus.Failed,
            TaskExecutionStatus.PermissionRequired => next is TaskExecutionStatus.Running
                or TaskExecutionStatus.Blocked
                or TaskExecutionStatus.Failed,
            TaskExecutionStatus.Blocked => next is TaskExecutionStatus.Running
                or TaskExecutionStatus.Failed,
            TaskExecutionStatus.Failed => false,
            TaskExecutionStatus.Completed => false,
            _ => false
        };
    }
}
