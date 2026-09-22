namespace MinhHuy.AIOffice.Shared.Contracts.Releases;

public enum CapabilityIsolationState
{
    Enabled,
    Isolated
}

public sealed record CapabilityIsolation(
    string TenantId,
    string CompanyId,
    string CapabilityId,
    CapabilityIsolationState State,
    string? IncidentId,
    string? IsolatedReleaseId)
{
    public static CapabilityIsolation Enabled(string tenantId, string companyId, string capabilityId)
    {
        Require(tenantId, nameof(tenantId));
        Require(companyId, nameof(companyId));
        Require(capabilityId, nameof(capabilityId));
        return new(tenantId, companyId, capabilityId, CapabilityIsolationState.Enabled, null, null);
    }

    public CapabilityIsolation Isolate(string tenantId, string companyId, string incidentId, string isolatedReleaseId)
    {
        AssertAuthority(tenantId, companyId);
        AssertState(CapabilityIsolationState.Enabled);
        Require(incidentId, nameof(incidentId));
        Require(isolatedReleaseId, nameof(isolatedReleaseId));
        return this with
        {
            State = CapabilityIsolationState.Isolated,
            IncidentId = incidentId,
            IsolatedReleaseId = isolatedReleaseId
        };
    }

    public CapabilityIsolation ResumeAfterHotfix(
        string tenantId,
        string companyId,
        string incidentId,
        ReleaseManifest hotfix,
        bool releaseGatesPassed)
    {
        AssertAuthority(tenantId, companyId);
        AssertState(CapabilityIsolationState.Isolated);
        Require(incidentId, nameof(incidentId));
        if (!StringComparer.Ordinal.Equals(IncidentId, incidentId))
        {
            throw new InvalidOperationException("Hotfix incident must match the active capability isolation incident.");
        }

        ArgumentNullException.ThrowIfNull(hotfix);
        hotfix.Validate();
        if (!releaseGatesPassed)
        {
            throw new InvalidOperationException("Release gates must pass before an isolated capability can resume.");
        }
        if (StringComparer.Ordinal.Equals(IsolatedReleaseId, hotfix.ReleaseId))
        {
            throw new InvalidOperationException("Capability cannot resume on the isolated release.");
        }

        return this with
        {
            State = CapabilityIsolationState.Enabled,
            IncidentId = null,
            IsolatedReleaseId = null
        };
    }

    public void AssertDispatchAllowed(string tenantId, string companyId)
    {
        AssertAuthority(tenantId, companyId);
        if (State == CapabilityIsolationState.Isolated)
        {
            throw new InvalidOperationException("Capability is isolated; new dispatch is denied while durable waiting work remains resumable.");
        }
    }

    private void AssertAuthority(string tenantId, string companyId)
    {
        Require(tenantId, nameof(tenantId));
        Require(companyId, nameof(companyId));
        if (!StringComparer.Ordinal.Equals(TenantId, tenantId) || !StringComparer.Ordinal.Equals(CompanyId, companyId))
        {
            throw new InvalidOperationException("Capability isolation authority mismatch.");
        }
    }

    private void AssertState(CapabilityIsolationState expected)
    {
        if (State != expected)
        {
            throw new InvalidOperationException($"Capability isolation requires state {expected}, current state is {State}.");
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
