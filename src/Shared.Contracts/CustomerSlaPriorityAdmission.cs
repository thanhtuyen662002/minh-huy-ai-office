namespace MinhHuyAiOffice.Shared.Contracts;

public sealed record CustomerSlaAuthority(
    string TenantId,
    string CompanyId,
    string UserId,
    long AuthorityVersion);

public sealed record CustomerSlaPolicy(
    string PolicyId,
    long PolicyVersion,
    IReadOnlyDictionary<string, int> ServiceClassPriorities,
    int PriorityCeiling);

public sealed record CustomerSlaPriorityAdmissionRequest(
    string AdmissionId,
    CustomerSlaAuthority Authority,
    CustomerSlaPolicy Policy,
    string RequestedServiceClass);

public sealed record CustomerSlaPriorityAdmissionEvidence(
    string AdmissionId,
    CustomerSlaAuthority Authority,
    string PolicyId,
    long PolicyVersion,
    string ServiceClass,
    int SchedulerPriority)
{
    public int PriorityCeiling { get; init; }
}

public static class CustomerSlaPriorityAdmission
{
    public static CustomerSlaPriorityAdmissionEvidence Decide(
        CustomerSlaPriorityAdmissionRequest request,
        CustomerSlaPriorityAdmissionEvidence? existing = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateCanonical(request.AdmissionId, nameof(request.AdmissionId));
        ValidateAuthority(request.Authority);
        ValidatePolicy(request.Policy);
        ValidateCanonical(request.RequestedServiceClass, nameof(request.RequestedServiceClass));

        if (!request.Policy.ServiceClassPriorities.TryGetValue(request.RequestedServiceClass, out var configuredPriority))
            throw new UnauthorizedAccessException("Requested service class is not allowed by the authoritative SLA policy.");
        if (configuredPriority < 0 || configuredPriority > request.Policy.PriorityCeiling)
            throw new InvalidOperationException("SLA policy contains a priority outside its authoritative ceiling.");

        var decision = new CustomerSlaPriorityAdmissionEvidence(
            request.AdmissionId,
            request.Authority,
            request.Policy.PolicyId,
            request.Policy.PolicyVersion,
            request.RequestedServiceClass,
            configuredPriority)
        {
            PriorityCeiling = request.Policy.PriorityCeiling,
        };

        if (existing is not null && existing != decision)
            throw new InvalidOperationException("Admission identity already has conflicting authority, policy, service class, priority, or ceiling evidence.");

        return existing ?? decision;
    }

    private static void ValidateAuthority(CustomerSlaAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ValidateCanonical(authority.TenantId, nameof(authority.TenantId));
        ValidateCanonical(authority.CompanyId, nameof(authority.CompanyId));
        ValidateCanonical(authority.UserId, nameof(authority.UserId));
        if (authority.AuthorityVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(authority.AuthorityVersion));
    }

    private static void ValidatePolicy(CustomerSlaPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ValidateCanonical(policy.PolicyId, nameof(policy.PolicyId));
        if (policy.PolicyVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(policy.PolicyVersion));
        if (policy.PriorityCeiling < 0)
            throw new ArgumentOutOfRangeException(nameof(policy.PriorityCeiling));
        ArgumentNullException.ThrowIfNull(policy.ServiceClassPriorities);

        foreach (var pair in policy.ServiceClassPriorities)
        {
            ValidateCanonical(pair.Key, nameof(policy.ServiceClassPriorities));
            if (pair.Value < 0 || pair.Value > policy.PriorityCeiling)
                throw new InvalidOperationException("SLA policy contains a priority outside its authoritative ceiling.");
        }
    }

    private static void ValidateCanonical(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            throw new ArgumentException($"{name} must be a canonical non-empty identifier.", name);
    }
}
