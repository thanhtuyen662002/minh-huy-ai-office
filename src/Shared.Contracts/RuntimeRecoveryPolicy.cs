namespace MinhHuyAiOffice.Shared.Contracts;

public enum RuntimeRecoveryKind
{
    RetryTransientOperation,
    RenewWorkerLease,
    RequeueDurableTask,
    RestartReplaceableWorker,
    ChangeSource,
    ChangeSchema,
    ChangeModel,
    ChangeWorkflow,
    ChangeSkill,
    ChangeRelease
}

public sealed record RuntimeRecoveryRequest(
    string TenantId,
    string CompanyId,
    string TaskId,
    string RecoveryId,
    RuntimeRecoveryKind Kind,
    int PriorHealingAttempts,
    bool PreAuthorized,
    bool Reversible);

public sealed record SelfImprovementEvidence(
    string CandidateId,
    string CandidateVersion,
    string SourceCommit,
    string EvalRunId,
    bool CiPassed,
    bool EvalPassed,
    bool ReleaseGatesPassed);

public sealed record RuntimeRecoveryDecision(
    bool HealingAllowed,
    bool ImprovementRequired,
    string TenantId,
    string CompanyId,
    string TaskId,
    string RecoveryId,
    string Reason);

public static class RuntimeRecoveryPolicy
{
    public const int MaxAutomaticHealingAttempts = 3;

    public static RuntimeRecoveryDecision EvaluateHealing(
        RuntimeRecoveryRequest request,
        string tenantId,
        string companyId)
    {
        ValidateRequest(request);
        RequireAuthority(request, tenantId, companyId);

        if (IsImprovement(request.Kind))
            return Escalate(request, "Versioned runtime mutation requires self-improvement gates.");

        if (!request.PreAuthorized || !request.Reversible)
            return Escalate(request, "Healing must be pre-authorized and reversible.");

        if (request.PriorHealingAttempts >= MaxAutomaticHealingAttempts)
            return Escalate(request, "Automatic healing attempt budget exhausted.");

        return new(true, false, request.TenantId, request.CompanyId, request.TaskId, request.RecoveryId,
            "Bounded runtime healing is authorized; durable task identity and version pins remain unchanged.");
    }

    public static RuntimeRecoveryDecision EvaluateImprovement(
        RuntimeRecoveryRequest request,
        string tenantId,
        string companyId,
        SelfImprovementEvidence evidence)
    {
        ValidateRequest(request);
        RequireAuthority(request, tenantId, companyId);
        ArgumentNullException.ThrowIfNull(evidence);

        if (!IsImprovement(request.Kind))
            throw new InvalidOperationException("Only versioned mutation kinds may enter the self-improvement gate.");

        Require(evidence.CandidateId, nameof(evidence.CandidateId));
        Require(evidence.CandidateVersion, nameof(evidence.CandidateVersion));
        Require(evidence.SourceCommit, nameof(evidence.SourceCommit));
        Require(evidence.EvalRunId, nameof(evidence.EvalRunId));

        if (!evidence.CiPassed || !evidence.EvalPassed || !evidence.ReleaseGatesPassed)
            return Escalate(request, "Self-improvement candidate has not passed CI, eval and release gates.");

        return new(false, false, request.TenantId, request.CompanyId, request.TaskId, request.RecoveryId,
            "Versioned self-improvement candidate is eligible for controlled release; existing task pins remain unchanged.");
    }

    private static bool IsImprovement(RuntimeRecoveryKind kind) => kind is
        RuntimeRecoveryKind.ChangeSource or
        RuntimeRecoveryKind.ChangeSchema or
        RuntimeRecoveryKind.ChangeModel or
        RuntimeRecoveryKind.ChangeWorkflow or
        RuntimeRecoveryKind.ChangeSkill or
        RuntimeRecoveryKind.ChangeRelease;

    private static RuntimeRecoveryDecision Escalate(RuntimeRecoveryRequest request, string reason) =>
        new(false, true, request.TenantId, request.CompanyId, request.TaskId, request.RecoveryId, reason);

    private static void ValidateRequest(RuntimeRecoveryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Require(request.TenantId, nameof(request.TenantId));
        Require(request.CompanyId, nameof(request.CompanyId));
        Require(request.TaskId, nameof(request.TaskId));
        Require(request.RecoveryId, nameof(request.RecoveryId));
        if (request.PriorHealingAttempts < 0)
            throw new ArgumentOutOfRangeException(nameof(request.PriorHealingAttempts));
    }

    private static void RequireAuthority(RuntimeRecoveryRequest request, string tenantId, string companyId)
    {
        Require(tenantId, nameof(tenantId));
        Require(companyId, nameof(companyId));
        if (!StringComparer.Ordinal.Equals(request.TenantId, tenantId) ||
            !StringComparer.Ordinal.Equals(request.CompanyId, companyId))
            throw new InvalidOperationException("Runtime recovery authority mismatch.");
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !StringComparer.Ordinal.Equals(value, value.Trim()))
            throw new ArgumentException($"{name} must be non-empty canonical text.", name);
    }
}
