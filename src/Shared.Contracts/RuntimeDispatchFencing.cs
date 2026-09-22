namespace MinhHuyAiOffice.Shared.Contracts;

public sealed record RuntimeDispatchIdentity(Guid TenantId, Guid CompanyId, Guid TaskId, Guid StepId)
{
    public string StableIdentity => $"{TenantId:N}:{CompanyId:N}:{TaskId:N}:{StepId:N}";

    public void Validate()
    {
        if (TenantId == Guid.Empty || CompanyId == Guid.Empty || TaskId == Guid.Empty || StepId == Guid.Empty)
            throw new InvalidOperationException("Dispatch identity requires tenant, company, task and step authority.");
    }
}

public sealed record RuntimeDispatchClaim(
    RuntimeDispatchIdentity Identity,
    long Epoch,
    string WorkerId,
    DateTimeOffset ClaimedAt,
    DateTimeOffset ExpiresAt);

public sealed record RuntimeDispatchCompletion(
    RuntimeDispatchIdentity Identity,
    long Epoch,
    string EvidenceId,
    DateTimeOffset PersistedAt);

public static class RuntimeDispatchFencing
{
    public static RuntimeDispatchClaim Claim(
        RuntimeDispatchIdentity identity,
        string workerId,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        RuntimeDispatchClaim? current = null)
    {
        identity.Validate();
        if (string.IsNullOrWhiteSpace(workerId)) throw new ArgumentException("WorkerId is required.", nameof(workerId));
        if (leaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration));

        if (current is null)
            return new(identity, 1, workerId, now, now.Add(leaseDuration));

        RequireSameIdentity(identity, current.Identity);
        if (now < current.ExpiresAt)
            throw new InvalidOperationException("Active dispatch lease cannot be stolen.");

        return new(identity, checked(current.Epoch + 1), workerId, now, now.Add(leaseDuration));
    }

    public static RuntimeDispatchClaim Renew(
        RuntimeDispatchClaim active,
        RuntimeDispatchIdentity identity,
        long epoch,
        string workerId,
        DateTimeOffset now,
        TimeSpan leaseDuration)
    {
        identity.Validate();
        RequireSameIdentity(identity, active.Identity);
        RequireActiveFence(active, epoch, workerId, now);
        if (leaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        return active with { ExpiresAt = now.Add(leaseDuration) };
    }

    public static RuntimeDispatchCompletion Complete(
        RuntimeDispatchClaim active,
        RuntimeDispatchIdentity identity,
        long epoch,
        string workerId,
        string evidenceId,
        DateTimeOffset now,
        RuntimeDispatchCompletion? persisted = null)
    {
        identity.Validate();
        RequireSameIdentity(identity, active.Identity);
        RequireActiveFence(active, epoch, workerId, now);
        if (string.IsNullOrWhiteSpace(evidenceId)) throw new ArgumentException("Completion evidence is required.", nameof(evidenceId));

        if (persisted is null)
            return new(identity, epoch, evidenceId, now);

        RequireSameIdentity(identity, persisted.Identity);
        if (persisted.Epoch != epoch || !string.Equals(persisted.EvidenceId, evidenceId, StringComparison.Ordinal))
            throw new InvalidOperationException("Conflicting completion evidence for dispatch fence.");
        return persisted;
    }

    public static void AuthorizeBrokerSettlement(
        RuntimeDispatchClaim active,
        RuntimeDispatchCompletion? persisted,
        RuntimeDispatchIdentity identity,
        long epoch)
    {
        identity.Validate();
        RequireSameIdentity(identity, active.Identity);
        if (persisted is null)
            throw new InvalidOperationException("Durable completion must be persisted before broker settlement.");
        RequireSameIdentity(identity, persisted.Identity);
        if (active.Epoch != epoch || persisted.Epoch != epoch)
            throw new InvalidOperationException("Broker settlement requires the exact active fencing epoch.");
    }

    private static void RequireActiveFence(RuntimeDispatchClaim active, long epoch, string workerId, DateTimeOffset now)
    {
        if (epoch != active.Epoch || !string.Equals(workerId, active.WorkerId, StringComparison.Ordinal))
            throw new InvalidOperationException("Stale or foreign dispatch fence.");
        if (now >= active.ExpiresAt)
            throw new InvalidOperationException("Dispatch lease has expired.");
    }

    private static void RequireSameIdentity(RuntimeDispatchIdentity expected, RuntimeDispatchIdentity actual)
    {
        if (expected != actual)
            throw new UnauthorizedAccessException("Dispatch authority cannot cross tenant/company/task/step boundary.");
    }
}
