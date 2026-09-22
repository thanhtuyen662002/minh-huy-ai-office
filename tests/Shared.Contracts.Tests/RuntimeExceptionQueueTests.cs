using MinhHuyAiOffice.Shared.Contracts;
using Xunit;

namespace MinhHuyAiOffice.Shared.Contracts.Tests;

public sealed class RuntimeExceptionQueueTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EnqueueIdentity_IsStableForDuplicateSourceOperation()
    {
        var first = Envelope();
        var duplicate = first with { ExceptionId = "exception-duplicate", EvidenceRef = "evidence-2" };

        Assert.Equal(RuntimeExceptionQueuePolicy.IdempotencyKey(first), RuntimeExceptionQueuePolicy.IdempotencyKey(duplicate));
    }

    [Fact]
    public void Claim_FailsClosedAcrossCompanyAuthority()
    {
        Assert.Throws<InvalidOperationException>(() => RuntimeExceptionQueuePolicy.Claim(
            Envelope(), "tenant", "other-company", "specialist", "claim-1", 1, Now.AddMinutes(5), now: Now));
    }

    [Fact]
    public void Claim_RejectsConcurrentOwnerAndAllowsNewerEpochAfterExpiry()
    {
        var envelope = Envelope();
        var active = RuntimeExceptionQueuePolicy.Claim(envelope, "tenant", "company", "specialist-a", "claim-1", 1, Now.AddMinutes(5), now: Now);

        Assert.Throws<InvalidOperationException>(() => RuntimeExceptionQueuePolicy.Claim(
            envelope, "tenant", "company", "specialist-b", "claim-2", 2, Now.AddMinutes(10), active, Now));

        var reclaimed = RuntimeExceptionQueuePolicy.Claim(
            envelope, "tenant", "company", "specialist-b", "claim-2", 2, Now.AddMinutes(10), active, Now.AddMinutes(6));

        Assert.Equal(2, reclaimed.LeaseEpoch);
        Assert.Equal("specialist-b", reclaimed.SpecialistId);
    }

    [Fact]
    public void Resolve_RequiresExactLiveClaimAndEvidenceAuthority()
    {
        var envelope = Envelope();
        var claim = RuntimeExceptionQueuePolicy.Claim(envelope, "tenant", "company", "specialist", "claim-1", 3, Now.AddMinutes(5), now: Now);
        var resolution = new RuntimeExceptionResolution("exception-1", "claim-1", "tenant", "company", "resolution-evidence", RuntimeExceptionDisposition.RetryDeterministicOperation);

        Assert.Equal(RuntimeExceptionDisposition.RetryDeterministicOperation,
            RuntimeExceptionQueuePolicy.Resolve(envelope, claim, resolution, "tenant", "company", 3, Now));
        Assert.Throws<InvalidOperationException>(() => RuntimeExceptionQueuePolicy.Resolve(
            envelope, claim, resolution with { CompanyId = "other-company" }, "tenant", "company", 3, Now));
        Assert.Throws<InvalidOperationException>(() => RuntimeExceptionQueuePolicy.Resolve(
            envelope, claim, resolution, "tenant", "company", 4, Now));
    }

    [Fact]
    public void Resolve_RejectsExpiredClaim()
    {
        var envelope = Envelope();
        var claim = RuntimeExceptionQueuePolicy.Claim(envelope, "tenant", "company", "specialist", "claim-1", 1, Now.AddMinutes(1), now: Now);
        var resolution = new RuntimeExceptionResolution("exception-1", "claim-1", "tenant", "company", "resolution-evidence", RuntimeExceptionDisposition.EscalateToSpecialist);

        Assert.Throws<InvalidOperationException>(() => RuntimeExceptionQueuePolicy.Resolve(
            envelope, claim, resolution, "tenant", "company", 1, Now.AddMinutes(2)));
    }

    private static RuntimeExceptionEnvelope Envelope() => new(
        "tenant", "company", "task-1", "exception-1", "operation-1",
        "accounting.post", "reconciliation-mismatch", "evidence-1", "audit-1",
        "release-1", "workflow-v1", "provider:model-v1");
}
