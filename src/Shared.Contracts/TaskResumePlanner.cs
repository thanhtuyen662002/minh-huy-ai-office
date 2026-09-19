namespace MinhHuy.AIOffice.Shared.Contracts;

public sealed record TaskStepRuntimeState(Guid StepId, TaskStepStatus Status);

public sealed record TaskDependencyState(Guid StepId, Guid DependsOnStepId);

public static class TaskResumePlanner
{
    public static IReadOnlyList<Guid> GetReadyStepIds(
        TaskExecutionStatus taskStatus,
        IEnumerable<TaskStepRuntimeState> steps,
        IEnumerable<TaskDependencyState> dependencies)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(dependencies);

        var stepList = steps.ToArray();
        var dependencyList = dependencies.ToArray();
        var stepById = stepList.ToDictionary(step => step.StepId);
        ValidateGraph(stepById, dependencyList);

        if (taskStatus is TaskExecutionStatus.ClarificationRequired
            or TaskExecutionStatus.PermissionRequired
            or TaskExecutionStatus.Blocked
            or TaskExecutionStatus.Failed
            or TaskExecutionStatus.Completed)
        {
            return [];
        }

        var dependenciesByStep = dependencyList
            .GroupBy(dependency => dependency.StepId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(dependency => dependency.DependsOnStepId).ToArray());

        return stepList
            .Where(step => step.Status is TaskStepStatus.Pending or TaskStepStatus.Ready)
            .Where(step =>
                !dependenciesByStep.TryGetValue(step.StepId, out var requiredStepIds)
                || requiredStepIds.All(requiredStepId =>
                    stepById[requiredStepId].Status == TaskStepStatus.Completed))
            .Select(step => step.StepId)
            .Order()
            .ToArray();
    }

    private static void ValidateGraph(
        IReadOnlyDictionary<Guid, TaskStepRuntimeState> stepById,
        IReadOnlyCollection<TaskDependencyState> dependencies)
    {
        if (stepById.Keys.Any(stepId => stepId == Guid.Empty))
        {
            throw new InvalidOperationException("Task step ids must be non-empty.");
        }

        foreach (var dependency in dependencies)
        {
            if (!stepById.ContainsKey(dependency.StepId)
                || !stepById.ContainsKey(dependency.DependsOnStepId))
            {
                throw new InvalidOperationException("Task dependency references an unknown step.");
            }

            if (dependency.StepId == dependency.DependsOnStepId)
            {
                throw new InvalidOperationException("Task step cannot depend on itself.");
            }
        }

        var duplicateDependency = dependencies
            .GroupBy(dependency => (dependency.StepId, dependency.DependsOnStepId))
            .Any(group => group.Count() > 1);

        if (duplicateDependency)
        {
            throw new InvalidOperationException("Task dependency graph contains duplicate edges.");
        }

        var incomingCount = stepById.Keys.ToDictionary(stepId => stepId, _ => 0);
        var dependents = stepById.Keys.ToDictionary(stepId => stepId, _ => new List<Guid>());

        foreach (var dependency in dependencies)
        {
            incomingCount[dependency.StepId]++;
            dependents[dependency.DependsOnStepId].Add(dependency.StepId);
        }

        var queue = new Queue<Guid>(
            incomingCount
                .Where(pair => pair.Value == 0)
                .Select(pair => pair.Key));

        var visited = 0;
        while (queue.TryDequeue(out var stepId))
        {
            visited++;
            foreach (var dependentStepId in dependents[stepId])
            {
                incomingCount[dependentStepId]--;
                if (incomingCount[dependentStepId] == 0)
                {
                    queue.Enqueue(dependentStepId);
                }
            }
        }

        if (visited != stepById.Count)
        {
            throw new InvalidOperationException("Task dependency graph contains a cycle.");
        }
    }
}
