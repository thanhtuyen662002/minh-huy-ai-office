namespace MinhHuyAiOffice.Shared.Contracts;

public enum RuntimeTaskClass
{
    Standard = 0,
    CustomerInteractive = 1,
    BusinessCritical = 2
}

public sealed record RuntimeSlaPolicy(
    Guid TenantId,
    Guid CompanyId,
    string PolicyId,
    long Version,
    int PriorityCeiling,
    TimeSpan StandardTarget,
    TimeSpan InteractiveTarget,
    TimeSpan CriticalTarget);

public sealed record RuntimeSchedulingAuthority(
    Guid TenantId,
    Guid CompanyId,
    string PolicyId,
    long PolicyVersion,
    int PriorityCeiling);

public sealed record RuntimeSchedulingSnapshot(
    Guid TenantId,
    Guid CompanyId,
    Guid TaskId,
    string PolicyId,
    long PolicyVersion,
    RuntimeTaskClass TaskClass,
    int EffectivePriority,
    DateTimeOffset EnqueuedAt,
    DateTimeOffset DueAt)
{
    public string StableIdentity => $"{TenantId:N}:{CompanyId:N}:{TaskId:N}:{PolicyId}:{PolicyVersion}";
}

public static class RuntimeSlaScheduler
{
    public static RuntimeSchedulingSnapshot Bind(
        RuntimeSlaPolicy policy,
        RuntimeSchedulingAuthority authority,
        Guid taskId,
        RuntimeTaskClass taskClass,
        int requestedPriority,
        DateTimeOffset enqueuedAt)
    {
        ValidatePolicy(policy, authority);
        if (taskId == Guid.Empty) throw new ArgumentException("TaskId is required.", nameof(taskId));
        if (!Enum.IsDefined(taskClass)) throw new InvalidOperationException("Unknown task classification.");
        if (requestedPriority < 0) throw new ArgumentOutOfRangeException(nameof(requestedPriority));

        var classFloor = taskClass switch
        {
            RuntimeTaskClass.Standard => 0,
            RuntimeTaskClass.CustomerInteractive => 25,
            RuntimeTaskClass.BusinessCritical => 50,
            _ => throw new InvalidOperationException("Unknown task classification.")
        };
        var effectivePriority = Math.Min(Math.Max(requestedPriority, classFloor), authority.PriorityCeiling);
        var target = taskClass switch
        {
            RuntimeTaskClass.Standard => policy.StandardTarget,
            RuntimeTaskClass.CustomerInteractive => policy.InteractiveTarget,
            RuntimeTaskClass.BusinessCritical => policy.CriticalTarget,
            _ => throw new InvalidOperationException("Unknown task classification.")
        };
        if (target <= TimeSpan.Zero) throw new InvalidOperationException("SLA target must be positive.");

        return new(policy.TenantId, policy.CompanyId, taskId, policy.PolicyId, policy.Version,
            taskClass, effectivePriority, enqueuedAt, enqueuedAt.Add(target));
    }

    public static RuntimeSchedulingSnapshot Resume(RuntimeSchedulingSnapshot snapshot) => snapshot;

    public static RuntimeSchedulingSnapshot Rebind(
        RuntimeSchedulingSnapshot current,
        RuntimeSlaPolicy policy,
        RuntimeSchedulingAuthority authority,
        int requestedPriority,
        DateTimeOffset reboundAt)
    {
        if (current.TenantId != authority.TenantId || current.CompanyId != authority.CompanyId)
            throw new UnauthorizedAccessException("Scheduling authority cannot cross tenant/company boundary.");
        if (policy.Version <= current.PolicyVersion)
            throw new InvalidOperationException("Policy rebind requires a newer authoritative version.");
        return Bind(policy, authority, current.TaskId, current.TaskClass, requestedPriority, reboundAt);
    }

    public static int Compare(RuntimeSchedulingSnapshot left, RuntimeSchedulingSnapshot right)
    {
        var priority = right.EffectivePriority.CompareTo(left.EffectivePriority);
        if (priority != 0) return priority;
        var deadline = left.DueAt.CompareTo(right.DueAt);
        if (deadline != 0) return deadline;
        return left.TaskId.CompareTo(right.TaskId);
    }

    private static void ValidatePolicy(RuntimeSlaPolicy policy, RuntimeSchedulingAuthority authority)
    {
        if (policy.TenantId == Guid.Empty || policy.CompanyId == Guid.Empty)
            throw new InvalidOperationException("Tenant/company policy scope is required.");
        if (policy.TenantId != authority.TenantId || policy.CompanyId != authority.CompanyId)
            throw new UnauthorizedAccessException("Scheduling policy authority scope mismatch.");
        if (string.IsNullOrWhiteSpace(policy.PolicyId) || policy.PolicyId != authority.PolicyId || policy.Version != authority.PolicyVersion)
            throw new InvalidOperationException("Scheduling policy identity/version mismatch.");
        if (policy.PriorityCeiling < 0 || authority.PriorityCeiling < 0 || authority.PriorityCeiling > policy.PriorityCeiling)
            throw new InvalidOperationException("Scheduling priority authority is invalid.");
    }
}
