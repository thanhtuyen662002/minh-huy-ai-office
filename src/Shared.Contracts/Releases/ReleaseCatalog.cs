namespace MinhHuy.AIOffice.Shared.Contracts.Releases;

public sealed record ReleaseManifest(
    string ReleaseId,
    string SourceCommit,
    string ComponentVersion,
    string SchemaContractVersion,
    string WorkflowVersion,
    string SkillVersion)
{
    public ReleaseManifest Validate()
    {
        Require(ReleaseId, nameof(ReleaseId));
        Require(SourceCommit, nameof(SourceCommit));
        Require(ComponentVersion, nameof(ComponentVersion));
        Require(SchemaContractVersion, nameof(SchemaContractVersion));
        Require(WorkflowVersion, nameof(WorkflowVersion));
        Require(SkillVersion, nameof(SkillVersion));
        return this;
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !StringComparer.Ordinal.Equals(value, value.Trim()))
        {
            throw new ArgumentException($"{name} must be non-empty canonical text.", name);
        }
    }
}

public sealed record TaskReleasePin(string TenantId, string CompanyId, string TaskId, string ReleaseId)
{
    public static TaskReleasePin Create(string tenantId, string companyId, string taskId, ReleaseManifest release)
    {
        ArgumentNullException.ThrowIfNull(release);
        release.Validate();
        Require(tenantId, nameof(tenantId));
        Require(companyId, nameof(companyId));
        Require(taskId, nameof(taskId));
        return new(tenantId, companyId, taskId, release.ReleaseId);
    }

    public void AssertAuthority(string tenantId, string companyId)
    {
        Require(tenantId, nameof(tenantId));
        Require(companyId, nameof(companyId));
        if (!StringComparer.Ordinal.Equals(TenantId, tenantId) || !StringComparer.Ordinal.Equals(CompanyId, companyId))
        {
            throw new InvalidOperationException("Task release pin authority mismatch.");
        }
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !StringComparer.Ordinal.Equals(value, value.Trim()))
        {
            throw new ArgumentException($"{name} must be non-empty canonical text.", name);
        }
    }
}

public enum ReleaseRolloutStage
{
    Registered,
    Canary,
    Promoted,
    DrainingPrevious,
    Stable,
    RolledBack
}

public sealed record ReleaseRollout(
    ReleaseManifest Candidate,
    string? PreviousReleaseId,
    ReleaseRolloutStage Stage)
{
    public static ReleaseRollout Register(ReleaseManifest candidate, string? previousReleaseId)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        candidate.Validate();
        if (previousReleaseId is not null)
        {
            Require(previousReleaseId, nameof(previousReleaseId));
            if (StringComparer.Ordinal.Equals(candidate.ReleaseId, previousReleaseId))
            {
                throw new ArgumentException("Previous release must differ from candidate.", nameof(previousReleaseId));
            }
        }
        return new(candidate, previousReleaseId, ReleaseRolloutStage.Registered);
    }

    public ReleaseRollout StartCanary(bool releaseGatesPassed)
    {
        AssertStage(ReleaseRolloutStage.Registered);
        if (!releaseGatesPassed)
        {
            throw new InvalidOperationException("Release gates must pass before canary rollout.");
        }
        return this with { Stage = ReleaseRolloutStage.Canary };
    }

    public ReleaseRollout Promote()
    {
        AssertStage(ReleaseRolloutStage.Canary);
        return this with { Stage = ReleaseRolloutStage.Promoted };
    }

    public ReleaseRollout BeginPreviousDrain()
    {
        AssertStage(ReleaseRolloutStage.Promoted);
        if (PreviousReleaseId is null)
        {
            return this with { Stage = ReleaseRolloutStage.Stable };
        }
        return this with { Stage = ReleaseRolloutStage.DrainingPrevious };
    }

    public ReleaseRollout CompleteDrain()
    {
        AssertStage(ReleaseRolloutStage.DrainingPrevious);
        return this with { Stage = ReleaseRolloutStage.Stable };
    }

    public ReleaseRollout Rollback(string targetReleaseId)
    {
        if (Stage is not (ReleaseRolloutStage.Canary or ReleaseRolloutStage.Promoted or ReleaseRolloutStage.DrainingPrevious))
        {
            throw new InvalidOperationException($"Cannot rollback from release stage {Stage}.");
        }
        Require(targetReleaseId, nameof(targetReleaseId));
        if (PreviousReleaseId is null || !StringComparer.Ordinal.Equals(PreviousReleaseId, targetReleaseId))
        {
            throw new InvalidOperationException("Rollback target must be the exact previous release.");
        }
        return this with { Stage = ReleaseRolloutStage.RolledBack };
    }

    private void AssertStage(ReleaseRolloutStage expected)
    {
        if (Stage != expected)
        {
            throw new InvalidOperationException($"Release progression requires stage {expected}, current stage is {Stage}.");
        }
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !StringComparer.Ordinal.Equals(value, value.Trim()))
        {
            throw new ArgumentException($"{name} must be non-empty canonical text.", name);
        }
    }
}
