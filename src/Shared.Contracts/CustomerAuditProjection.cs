namespace MinhHuyAiOffice.Shared.Contracts;

public sealed record CustomerAuditAuthority(Guid TenantId, Guid CompanyId)
{
    public void Validate()
    {
        if (TenantId == Guid.Empty || CompanyId == Guid.Empty)
            throw new InvalidOperationException("Customer audit authority requires tenant and company.");
    }
}

public sealed record CustomerAuditEvent(
    CustomerAuditAuthority Authority,
    Guid EventId,
    long Version,
    DateTimeOffset OccurredAt,
    string ActorId,
    string Action,
    string Resource,
    string EvidenceReference);

public sealed record CustomerAuditRow(
    Guid EventId,
    long Version,
    DateTimeOffset OccurredAt,
    string ActorId,
    string Action,
    string Resource,
    string EvidenceReference);

public sealed record CustomerAuditCursor(CustomerAuditAuthority Authority, long Version, Guid EventId);

public static class CustomerAuditProjection
{
    public const string ReadCapability = "customer.audit.read";

    public static IReadOnlyList<CustomerAuditRow> Project(
        CustomerAuditAuthority authority,
        IEnumerable<CustomerAuditEvent> events,
        CustomerAuditCursor? after = null)
    {
        authority.Validate();
        if (events is null) throw new ArgumentNullException(nameof(events));
        if (after is not null)
        {
            RequireSameAuthority(authority, after.Authority);
            if (after.Version < 0) throw new InvalidOperationException("Audit cursor version cannot be negative.");
        }

        var unique = new Dictionary<Guid, CustomerAuditEvent>();
        foreach (var auditEvent in events)
        {
            ValidateEvent(authority, auditEvent);
            if (unique.TryGetValue(auditEvent.EventId, out var existing))
            {
                if (existing != auditEvent)
                    throw new InvalidOperationException("Conflicting durable audit evidence for the same event identity.");
                continue;
            }
            unique.Add(auditEvent.EventId, auditEvent);
        }

        return unique.Values
            .Where(e => after is null || e.Version > after.Version || (e.Version == after.Version && e.EventId.CompareTo(after.EventId) > 0))
            .OrderBy(e => e.OccurredAt)
            .ThenBy(e => e.Version)
            .ThenBy(e => e.EventId)
            .Select(e => new CustomerAuditRow(e.EventId, e.Version, e.OccurredAt, e.ActorId, e.Action, e.Resource, e.EvidenceReference))
            .ToArray();
    }

    public static CustomerAuditCursor NextCursor(CustomerAuditAuthority authority, IReadOnlyList<CustomerAuditRow> rows, CustomerAuditCursor? current = null)
    {
        authority.Validate();
        if (current is not null) RequireSameAuthority(authority, current.Authority);
        if (rows.Count == 0) return current ?? new CustomerAuditCursor(authority, 0, Guid.Empty);

        var max = rows.OrderBy(r => r.Version).ThenBy(r => r.EventId).Last();
        if (current is not null && (max.Version < current.Version || (max.Version == current.Version && max.EventId.CompareTo(current.EventId) < 0)))
            throw new InvalidOperationException("Audit cursor cannot move backward.");
        return new CustomerAuditCursor(authority, max.Version, max.EventId);
    }

    public static void AuthorizeRead(CustomerAuditAuthority authority, CustomerAuditAuthority grantedAuthority, IEnumerable<string> capabilities)
    {
        authority.Validate();
        RequireSameAuthority(authority, grantedAuthority);
        if (capabilities is null || !capabilities.Contains(ReadCapability, StringComparer.Ordinal))
            throw new UnauthorizedAccessException("Customer audit read capability is required.");
    }

    private static void ValidateEvent(CustomerAuditAuthority authority, CustomerAuditEvent auditEvent)
    {
        RequireSameAuthority(authority, auditEvent.Authority);
        if (auditEvent.EventId == Guid.Empty || auditEvent.Version <= 0)
            throw new InvalidOperationException("Audit event requires durable identity and positive version.");
        if (string.IsNullOrWhiteSpace(auditEvent.ActorId) || string.IsNullOrWhiteSpace(auditEvent.Action) || string.IsNullOrWhiteSpace(auditEvent.Resource) || string.IsNullOrWhiteSpace(auditEvent.EvidenceReference))
            throw new InvalidOperationException("Audit event metadata and opaque evidence reference are required.");
        if (auditEvent.EvidenceReference.Contains("password=", StringComparison.OrdinalIgnoreCase) ||
            auditEvent.EvidenceReference.Contains("connection string", StringComparison.OrdinalIgnoreCase) ||
            auditEvent.EvidenceReference.Contains("secret=", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Audit projection cannot expose secret-bearing evidence.");
    }

    private static void RequireSameAuthority(CustomerAuditAuthority expected, CustomerAuditAuthority actual)
    {
        if (expected != actual)
            throw new UnauthorizedAccessException("Customer audit authority cannot cross tenant/company boundary.");
    }
}
