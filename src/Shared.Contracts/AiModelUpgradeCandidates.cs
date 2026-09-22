namespace MinhHuyAiOffice.Shared.Contracts;

public sealed record AiModelUpgradeCandidate(
    string CandidateId,
    string CandidateVersion,
    string SourceCommit,
    string TenantId,
    string CompanyId,
    AiCapability RequiredCapability,
    AiModelProfile Current,
    AiModelProfile Proposed)
{
    public void Validate()
    {
        Require(CandidateId, nameof(CandidateId));
        Require(CandidateVersion, nameof(CandidateVersion));
        Require(SourceCommit, nameof(SourceCommit));
        Require(TenantId, nameof(TenantId));
        Require(CompanyId, nameof(CompanyId));
        ArgumentNullException.ThrowIfNull(Current);
        ArgumentNullException.ThrowIfNull(Proposed);
        Current.Validate();
        Proposed.Validate();

        if (!Proposed.IsHealthy || !Proposed.Capabilities.Contains(RequiredCapability))
            throw new InvalidOperationException("Proposed model must be healthy and support the required capability.");
        if (Proposed.Tier < Current.Tier)
            throw new InvalidOperationException("Model upgrade cannot regress capability tier.");
        if (Proposed.MaxCostPerMillionTokens > Current.MaxCostPerMillionTokens)
            throw new InvalidOperationException("Model upgrade cannot exceed the current cost ceiling without a separate policy decision.");
        if (Proposed.TypicalLatencyMs > Current.TypicalLatencyMs)
            throw new InvalidOperationException("Model upgrade cannot regress the current latency ceiling.");
        if (StringComparer.Ordinal.Equals(Current.ProviderId, Proposed.ProviderId) &&
            StringComparer.Ordinal.Equals(Current.ModelId, Proposed.ModelId))
            throw new InvalidOperationException("Upgrade candidate must propose a different provider/model identity.");
    }

    public AiModelUpgradeActivation Activate(
        string tenantId,
        string companyId,
        AiModelUpgradeEvalEvidence evidence,
        bool releaseGatesPassed)
    {
        Validate();
        Require(tenantId, nameof(tenantId));
        Require(companyId, nameof(companyId));
        if (!StringComparer.Ordinal.Equals(TenantId, tenantId) || !StringComparer.Ordinal.Equals(CompanyId, companyId))
            throw new InvalidOperationException("Model upgrade authority mismatch.");
        ArgumentNullException.ThrowIfNull(evidence);
        evidence.Validate();
        if (!StringComparer.Ordinal.Equals(CandidateId, evidence.CandidateId) ||
            !StringComparer.Ordinal.Equals(CandidateVersion, evidence.CandidateVersion) ||
            !StringComparer.Ordinal.Equals(SourceCommit, evidence.SourceCommit))
            throw new InvalidOperationException("Eval evidence must match the exact upgrade candidate version and source commit.");
        if (!evidence.Passed || !releaseGatesPassed)
            throw new InvalidOperationException("Eval and release gates must pass before model upgrade activation.");

        return new(
            TenantId,
            CompanyId,
            CandidateId,
            CandidateVersion,
            Proposed.ProviderId,
            Proposed.ModelId,
            Current.ProviderId,
            Current.ModelId);
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !StringComparer.Ordinal.Equals(value, value.Trim()))
            throw new ArgumentException($"{name} must be non-empty canonical text.", name);
    }
}

public sealed record AiModelUpgradeEvalEvidence(
    string CandidateId,
    string CandidateVersion,
    string SourceCommit,
    string EvalRunId,
    bool Passed)
{
    public void Validate()
    {
        Require(CandidateId, nameof(CandidateId));
        Require(CandidateVersion, nameof(CandidateVersion));
        Require(SourceCommit, nameof(SourceCommit));
        Require(EvalRunId, nameof(EvalRunId));
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !StringComparer.Ordinal.Equals(value, value.Trim()))
            throw new ArgumentException($"{name} must be non-empty canonical text.", name);
    }
}

public sealed record AiModelUpgradeActivation(
    string TenantId,
    string CompanyId,
    string CandidateId,
    string CandidateVersion,
    string ProviderId,
    string ModelId,
    string PreviousProviderId,
    string PreviousModelId)
{
    public AiModelSelection Rollback(string tenantId, string companyId)
    {
        if (!StringComparer.Ordinal.Equals(TenantId, tenantId) || !StringComparer.Ordinal.Equals(CompanyId, companyId))
            throw new InvalidOperationException("Model rollback authority mismatch.");

        return new(TenantId, CompanyId, PreviousProviderId, PreviousModelId);
    }
}

public sealed record AiModelSelection(
    string TenantId,
    string CompanyId,
    string ProviderId,
    string ModelId);

public sealed record AiTaskModelPin(
    string TenantId,
    string CompanyId,
    string TaskId,
    string ProviderId,
    string ModelId)
{
    public void AssertAuthority(string tenantId, string companyId)
    {
        if (!StringComparer.Ordinal.Equals(TenantId, tenantId) || !StringComparer.Ordinal.Equals(CompanyId, companyId))
            throw new InvalidOperationException("Task model pin authority mismatch.");
    }
}
