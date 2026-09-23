using MinhHuyAiOffice.Shared.Contracts;
using Xunit;

namespace MinhHuyAiOffice.Shared.Contracts.Tests;

public sealed class RuntimeRecoveryFencingTests
{
    private readonly RuntimeRecoveryIdentity identity = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private RuntimeDurableCheckpoint Checkpoint(long version = 3) => new(identity, version, $"checkpoint-{version}", Now);

    [Fact]
    public void Restore_lease_binds_exact_authority_checkpoint_and_generation()
    {
        var lease = RuntimeRecoveryFencing.Begin(identity, Checkpoint(), 7, "site-b", Now.AddMinutes(1));
        Assert.Equal(7, lease.Generation);
        Assert.Equal(3, lease.CheckpointVersion);
        Assert.Equal(identity.StableIdentity, lease.Identity.StableIdentity);
    }

    [Fact]
    public void Cross_company_checkpoint_fails_closed()
    {
        var foreign = identity with { CompanyId = Guid.NewGuid() };
        var checkpoint = new RuntimeDurableCheckpoint(foreign, 3, "checkpoint", Now);
        Assert.Throws<UnauthorizedAccessException>(() => RuntimeRecoveryFencing.Begin(identity, checkpoint, 1, "site-b", Now));
    }

    [Fact]
    public void Recovery_generation_must_advance_and_checkpoint_cannot_roll_back()
    {
        var first = RuntimeRecoveryFencing.Begin(identity, Checkpoint(3), 4, "site-b", Now);
        Assert.Throws<InvalidOperationException>(() => RuntimeRecoveryFencing.Begin(identity, Checkpoint(4), 4, "site-c", Now, first));
        Assert.Throws<InvalidOperationException>(() => RuntimeRecoveryFencing.Begin(identity, Checkpoint(2), 5, "site-c", Now, first));
        var next = RuntimeRecoveryFencing.Begin(identity, Checkpoint(4), 5, "site-c", Now, first);
        Assert.Equal(4, next.CheckpointVersion);
    }

    [Fact]
    public void Stale_or_foreign_recovery_fence_cannot_complete()
    {
        var checkpoint = Checkpoint();
        var lease = RuntimeRecoveryFencing.Begin(identity, checkpoint, 7, "site-b", Now);
        Assert.Throws<InvalidOperationException>(() => RuntimeRecoveryFencing.Complete(lease, checkpoint, 6, "site-b", "restore", Now));
        Assert.Throws<InvalidOperationException>(() => RuntimeRecoveryFencing.Complete(lease, checkpoint, 7, "site-c", "restore", Now));
    }

    [Fact]
    public void Completion_requires_exact_checkpoint_version()
    {
        var lease = RuntimeRecoveryFencing.Begin(identity, Checkpoint(3), 7, "site-b", Now);
        Assert.Throws<InvalidOperationException>(() => RuntimeRecoveryFencing.Complete(lease, Checkpoint(4), 7, "site-b", "restore", Now));
    }

    [Fact]
    public void Exact_completion_replay_is_idempotent_but_conflicting_evidence_is_rejected()
    {
        var checkpoint = Checkpoint();
        var lease = RuntimeRecoveryFencing.Begin(identity, checkpoint, 7, "site-b", Now);
        var first = RuntimeRecoveryFencing.Complete(lease, checkpoint, 7, "site-b", "restore-1", Now.AddMinutes(1));
        var duplicate = RuntimeRecoveryFencing.Complete(lease, checkpoint, 7, "site-b", "restore-1", Now.AddMinutes(2), first);
        Assert.Same(first, duplicate);
        Assert.Throws<InvalidOperationException>(() => RuntimeRecoveryFencing.Complete(lease, checkpoint, 7, "site-b", "restore-2", Now.AddMinutes(2), first));
    }

    [Fact]
    public void Dispatch_resume_requires_persisted_exact_recovery_completion()
    {
        var checkpoint = Checkpoint();
        var lease = RuntimeRecoveryFencing.Begin(identity, checkpoint, 7, "site-b", Now);
        Assert.Throws<InvalidOperationException>(() => RuntimeRecoveryFencing.AuthorizeDispatchResume(lease, null, 7));
        var persisted = RuntimeRecoveryFencing.Complete(lease, checkpoint, 7, "site-b", "restore", Now.AddMinutes(1));
        RuntimeRecoveryFencing.AuthorizeDispatchResume(lease, persisted, 7);
        Assert.Throws<InvalidOperationException>(() => RuntimeRecoveryFencing.AuthorizeDispatchResume(lease, persisted, 8));
    }

    [Fact]
    public void Malformed_checkpoint_and_generation_fail_closed()
    {
        var invalid = new RuntimeDurableCheckpoint(identity, 0, "checkpoint", Now);
        Assert.Throws<InvalidOperationException>(() => RuntimeRecoveryFencing.Begin(identity, invalid, 1, "site-b", Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => RuntimeRecoveryFencing.Begin(identity, Checkpoint(), 0, "site-b", Now));
    }
}
