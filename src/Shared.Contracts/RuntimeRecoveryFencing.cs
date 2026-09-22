namespace MinhHuyAiOffice.Shared.Contracts;

public sealed record RuntimeRecoveryIdentity(Guid TenantId, Guid CompanyId, Guid TaskId, Guid StepId)
{
    public string StableIdentity => $"{TenantId:N}:{CompanyId:N}:{TaskId:N}:{StepId:N}";

    public void Validate()
    {
        if (TenantId == Guid.Empty || CompanyId == Guid.Empty || TaskId == Guid.Empty || StepId == Guid.Empty)
            throw new InvalidOperationException("Recovery identity requires tenant, company, task and step authority.");
    }
}

public sealed record RuntimeDurableCheckpoint(
    RuntimeRecoveryIdentity Identity,
    long Version,
    string EvidenceId,
    DateTimeOffset PersistedAt);

public sealed record RuntimeRecoveryLease(
    RuntimeRecoveryIdentity Identity,
    long Generation,
    long CheckpointVersion,
    string RecoveryNodeId,
    DateTimeOffset StartedAt);

public sealed record RuntimeRecoveryCompletion(
    RuntimeRecoveryIdentity Identity,
    long Generation,
    long CheckpointVersion,
    string RestoreEvidenceId,
    DateTimeOffset PersistedAt);

public static class RuntimeRecoveryFencing
{
    public static RuntimeRecoveryLease Begin(
        RuntimeRecoveryIdentity identity,
        RuntimeDurableCheckpoint checkpoint,
        long generation,
        string recoveryNodeId,
        RuntimeRecoveryLease? latest = null)
    {
        identity.Validate();
        RequireSameIdentity(identity, checkpoint.Identity);
        if (checkpoint.Version <= 0) throw new InvalidOperationException("Checkpoint version must be positive.");
        if (string.IsNullOrWhiteSpace(checkpoint.EvidenceId)) throw new InvalidOperationException("Checkpoint evidence is required.");
        if (generation <= 0) throw new ArgumentOutOfRangeException(nameof(generation));
        if (string.IsNullOrWhiteSpace(recoveryNodeId)) throw new ArgumentException("Recovery node is required.", nameof(recoveryNodeId));

        if (latest is not null)
        {
            RequireSameIdentity(identity, latest.Identity);
            if (generation <= latest.Generation)
                throw new InvalidOperationException("Recovery generation must advance monotonically.");
            if (checkpoint.Version < latest.CheckpointVersion)
                throw new InvalidOperationException("Recovery cannot roll durable checkpoint state backward.");
        }

        return new(identity, generation, checkpoint.Version, recoveryNodeId, DateTimeOffset.UtcNow);
    }

    public static RuntimeRecoveryCompletion Complete(
        RuntimeRecoveryLease active,
        RuntimeDurableCheckpoint checkpoint,
        long generation,
        string recoveryNodeId,
        string restoreEvidenceId,
        DateTimeOffset persistedAt,
        RuntimeRecoveryCompletion? persisted = null)
    {
        RequireSameIdentity(active.Identity, checkpoint.Identity);
        RequireFence(active, generation, recoveryNodeId);
        if (checkpoint.Version != active.CheckpointVersion)
            throw new InvalidOperationException("Restore must use the exact fenced checkpoint version.");
        if (string.IsNullOrWhiteSpace(restoreEvidenceId)) throw new ArgumentException("Restore evidence is required.", nameof(restoreEvidenceId));

        if (persisted is null)
            return new(active.Identity, generation, checkpoint.Version, restoreEvidenceId, persistedAt);

        RequireSameIdentity(active.Identity, persisted.Identity);
        if (persisted.Generation != generation || persisted.CheckpointVersion != checkpoint.Version ||
            !string.Equals(persisted.RestoreEvidenceId, restoreEvidenceId, StringComparison.Ordinal))
            throw new InvalidOperationException("Conflicting persisted recovery evidence.");
        return persisted;
    }

    public static void AuthorizeDispatchResume(
        RuntimeRecoveryLease active,
        RuntimeRecoveryCompletion? persisted,
        long generation)
    {
        if (persisted is null)
            throw new InvalidOperationException("Recovery completion must be persisted before dispatch resume.");
        RequireSameIdentity(active.Identity, persisted.Identity);
        if (active.Generation != generation || persisted.Generation != generation ||
            active.CheckpointVersion != persisted.CheckpointVersion)
            throw new InvalidOperationException("Dispatch resume requires the exact persisted recovery fence.");
    }

    private static void RequireFence(RuntimeRecoveryLease active, long generation, string recoveryNodeId)
    {
        if (active.Generation != generation || !string.Equals(active.RecoveryNodeId, recoveryNodeId, StringComparison.Ordinal))
            throw new InvalidOperationException("Stale or foreign recovery fence.");
    }

    private static void RequireSameIdentity(RuntimeRecoveryIdentity expected, RuntimeRecoveryIdentity actual)
    {
        if (expected != actual)
            throw new UnauthorizedAccessException("Recovery authority cannot cross tenant/company/task/step boundary.");
    }
}
