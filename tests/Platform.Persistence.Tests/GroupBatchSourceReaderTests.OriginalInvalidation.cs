using System.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Fact]
    public async Task OriginalInvalidationProofAcceptsOriginalOrLaterCheckpointClaimAndExactRepeatWithoutEffects()
    {
        using var f = new Fixture(); var input = await InvalidationProofInputAsync(f);
        var before = await InvalidationEffectsAsync(f); var keys = f.Keys.Reads;
        var encoded = input.Original.Plan.Encode(); var digest = input.Row.CheckpointSha256;
        RequireInvalidationProof(input); RequireInvalidationProof(input);
        GroupOriginalInvalidationProof.Require(input.Row, input.Handle.Receipt.Scope, input.Row.BatchId,
            input.Original, input.Claim, input.Row.CreatedAtUtc);
        Assert.Equal(digest, GroupOriginalInvalidationProof.Digest(input.Row));
        var atIssue = CopyInvalidationRow(input.Row); atIssue.CreatedAtUtc = input.Claim.IssuedAtUtc;
        atIssue.CheckpointSha256 = GroupOriginalInvalidationProof.Digest(atIssue);
        GroupOriginalInvalidationProof.Require(atIssue, input.Handle.Receipt.Scope, atIssue.BatchId,
            input.Original, input.Claim, input.Now);
        var later = CopyInvalidationClaim(input.Claim);
        later.OperationId = Guid.NewGuid(); later.Epoch++;
        var row = CopyInvalidationRow(input.Row);
        row.ClaimOperationId = later.OperationId; row.ClaimEpoch = later.Epoch;
        row.CheckpointSha256 = GroupOriginalInvalidationProof.Digest(row);
        GroupOriginalInvalidationProof.Require(row, input.Handle.Receipt.Scope, row.BatchId, input.Original, later, input.Now);
        Assert.Equal(encoded, input.Original.Plan.Encode()); Assert.Equal(digest, input.Row.CheckpointSha256);
        Assert.Equal(before, await InvalidationEffectsAsync(f)); Assert.Equal(keys, f.Keys.Reads);
        Assert.False(f.Auth.Db.ChangeTracker.HasChanges());
        // Real original plan/handle from the fixture, synthetic persisted-claim shape only.
        // No claim that this pure proof test committed or authorized a SQL checkpoint.
    }

    [Theory]
    [InlineData("tenant", false)]
    [InlineData("tenant", true)]
    [InlineData("company", false)]
    [InlineData("company", true)]
    [InlineData("binding", false)]
    [InlineData("binding", true)]
    [InlineData("batch", false)]
    [InlineData("batch", true)]
    [InlineData("format", false)]
    [InlineData("format", true)]
    [InlineData("plan-hash", false)]
    [InlineData("plan-hash", true)]
    [InlineData("original-operation-empty", false)]
    [InlineData("original-operation-empty", true)]
    [InlineData("original-operation", false)]
    [InlineData("original-operation", true)]
    [InlineData("original-epoch-zero", false)]
    [InlineData("original-epoch-zero", true)]
    [InlineData("original-epoch", false)]
    [InlineData("original-epoch", true)]
    [InlineData("checkpoint-operation-empty", false)]
    [InlineData("checkpoint-operation-empty", true)]
    [InlineData("checkpoint-operation", false)]
    [InlineData("checkpoint-operation", true)]
    [InlineData("checkpoint-epoch-zero", false)]
    [InlineData("checkpoint-epoch-zero", true)]
    [InlineData("checkpoint-epoch", false)]
    [InlineData("checkpoint-epoch", true)]
    [InlineData("before-original", false)]
    [InlineData("before-original", true)]
    [InlineData("at-claim-expiry", false)]
    [InlineData("at-claim-expiry", true)]
    [InlineData("future", false)]
    [InlineData("future", true)]
    [InlineData("non-utc", false)]
    [InlineData("non-utc", true)]
    public async Task OriginalInvalidationProofRejectsChangedRootedFieldsEvenAfterResealing(string fault, bool reseal)
    {
        using var f = new Fixture(); var input = await InvalidationProofInputAsync(f);
        var row = CopyInvalidationRow(input.Row); var keys = f.Keys.Reads; var now = input.Now;
        switch (fault)
        {
            case "tenant": row.TenantId = Guid.NewGuid(); break;
            case "company": row.CompanyId = Guid.NewGuid(); break;
            case "binding": row.BindingId = Guid.NewGuid(); break;
            case "batch": row.BatchId = Guid.NewGuid(); break;
            case "format": row.FormatVersion = 2; break;
            case "plan-hash": row.PlanSha256 = new string('B', 64); break;
            case "original-operation-empty": row.OriginalClaimOperationId = Guid.Empty; break;
            case "original-operation": row.OriginalClaimOperationId = Guid.NewGuid(); break;
            case "original-epoch-zero": row.OriginalClaimEpoch = 0; break;
            case "original-epoch": row.OriginalClaimEpoch++; break;
            case "checkpoint-operation-empty": row.ClaimOperationId = Guid.Empty; break;
            case "checkpoint-operation": row.ClaimOperationId = Guid.NewGuid(); break;
            case "checkpoint-epoch-zero": row.ClaimEpoch = 0; break;
            case "checkpoint-epoch": row.ClaimEpoch++; break;
            case "before-original": row.CreatedAtUtc = input.Original.CreatedAtUtc.AddTicks(-1); break;
            case "at-claim-expiry": row.CreatedAtUtc = input.Claim.ExpiresAtUtc; now = row.CreatedAtUtc; break;
            case "future": row.CreatedAtUtc = input.Now.AddTicks(1); break;
            case "non-utc": row.CreatedAtUtc = row.CreatedAtUtc.ToOffset(TimeSpan.FromHours(7)); break;
            default: throw new InvalidOperationException("Unknown test fault.");
        }
        Assert.NotEqual(input.Row.CheckpointSha256, GroupOriginalInvalidationProof.Digest(row));
        if (reseal) row.CheckpointSha256 = GroupOriginalInvalidationProof.Digest(row);
        AssertInvalidationProofRefused(() => GroupOriginalInvalidationProof.Require(row, input.Handle.Receipt.Scope,
            input.Row.BatchId, input.Original, input.Claim, now));
        Assert.Equal(keys, f.Keys.Reads); Assert.False(f.Auth.Db.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("company")]
    [InlineData("binding")]
    [InlineData("batch")]
    [InlineData("operation")]
    [InlineData("epoch")]
    [InlineData("authority")]
    [InlineData("issued-after-checkpoint")]
    [InlineData("expiry-at-checkpoint")]
    [InlineData("issued-non-utc")]
    [InlineData("expiry-non-utc")]
    [InlineData("owner-empty")]
    [InlineData("service-empty")]
    [InlineData("credential-zero")]
    [InlineData("grant-zero")]
    [InlineData("source-zero")]
    [InlineData("deletion-negative")]
    [InlineData("account-zero")]
    [InlineData("lifetime-below-minimum")]
    [InlineData("lifetime-above-maximum")]
    [InlineData("lifetime-mismatch")]
    public async Task OriginalInvalidationProofChecksIndependentPersistedClaimRatherThanOnlySelfDigest(string fault)
    {
        using var f = new Fixture(); var input = await InvalidationProofInputAsync(f);
        var claim = CopyInvalidationClaim(input.Claim); var keys = f.Keys.Reads;
        switch (fault)
        {
            case "tenant": claim.TenantId = Guid.NewGuid(); break;
            case "company": claim.CompanyId = Guid.NewGuid(); break;
            case "binding": claim.BindingId = Guid.NewGuid(); break;
            case "batch": claim.BatchId = Guid.NewGuid(); break;
            case "operation": claim.OperationId = Guid.NewGuid(); break;
            case "epoch": claim.Epoch++; break;
            case "authority": claim.AuthoritySha256 = new string('B', 64); break;
            case "issued-after-checkpoint": claim.IssuedAtUtc = input.Row.CreatedAtUtc.AddTicks(1); break;
            case "expiry-at-checkpoint": claim.ExpiresAtUtc = input.Row.CreatedAtUtc; break;
            case "issued-non-utc": claim.IssuedAtUtc = claim.IssuedAtUtc.ToOffset(TimeSpan.FromHours(7)); break;
            case "expiry-non-utc": claim.ExpiresAtUtc = claim.ExpiresAtUtc.ToOffset(TimeSpan.FromHours(7)); break;
            case "owner-empty": claim.OwnerId = Guid.Empty; break;
            case "service-empty": claim.ServiceId = Guid.Empty; break;
            case "credential-zero": claim.CredentialEpoch = 0; break;
            case "grant-zero": claim.GrantVersion = 0; break;
            case "source-zero": claim.SourceVersion = 0; break;
            case "deletion-negative": claim.DeletionGeneration = -1; break;
            case "account-zero": claim.AccountVersion = 0; break;
            case "lifetime-below-minimum": claim.RequestedLifetimeTicks = GroupBatchClaimStore.MinimumLifetime.Ticks - 1; break;
            case "lifetime-above-maximum": claim.RequestedLifetimeTicks = GroupBatchClaimStore.MaximumLifetime.Ticks + 1; break;
            case "lifetime-mismatch": claim.RequestedLifetimeTicks++; break;
            default: throw new InvalidOperationException("Unknown test fault.");
        }
        Assert.Equal(input.Row.CheckpointSha256, GroupOriginalInvalidationProof.Digest(input.Row));
        AssertInvalidationProofRefused(() => GroupOriginalInvalidationProof.Require(input.Row, input.Handle.Receipt.Scope,
            input.Row.BatchId, input.Original, claim, input.Now));
        Assert.Equal(keys, f.Keys.Reads); Assert.False(f.Auth.Db.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData("scope-tenant")]
    [InlineData("scope-company")]
    [InlineData("scope-binding")]
    [InlineData("batch-empty")]
    [InlineData("batch")]
    [InlineData("original-plan")]
    [InlineData("original-operation")]
    [InlineData("original-epoch")]
    [InlineData("original-created-after-checkpoint")]
    [InlineData("original-created-non-utc")]
    [InlineData("now-before-checkpoint")]
    [InlineData("now-non-utc")]
    public async Task OriginalInvalidationProofRejectsChangedIndependentOriginalScopeAndClock(string fault)
    {
        using var f = new Fixture(); var input = await InvalidationProofInputAsync(f);
        var scope = input.Handle.Receipt.Scope; var batch = input.Row.BatchId;
        var original = input.Original; var now = input.Now; var keys = f.Keys.Reads;
        switch (fault)
        {
            case "scope-tenant": scope = scope with { TenantId = Guid.NewGuid() }; break;
            case "scope-company": scope = scope with { CompanyId = Guid.NewGuid() }; break;
            case "scope-binding": scope = scope with { SourceBindingId = Guid.NewGuid() }; break;
            case "batch-empty": batch = Guid.Empty; break;
            case "batch": batch = Guid.NewGuid(); break;
            case "original-plan":
                var chunk = input.Chunks[0];
                var other = GroupAutomaticInputPlan.Create(chunk.Current.Preparation, chunk.Brain,
                    new string('B', 64), "model", 64000, []);
                original = original with { Plan = other }; break;
            case "original-operation": original = original with { OriginalClaimOperationId = Guid.NewGuid() }; break;
            case "original-epoch": original = original with { OriginalClaimEpoch = original.OriginalClaimEpoch + 1 }; break;
            case "original-created-after-checkpoint": original = original with { CreatedAtUtc = input.Row.CreatedAtUtc.AddTicks(1) }; break;
            case "original-created-non-utc": original = original with { CreatedAtUtc = original.CreatedAtUtc.ToOffset(TimeSpan.FromHours(7)) }; break;
            case "now-before-checkpoint": now = input.Row.CreatedAtUtc.AddTicks(-1); break;
            case "now-non-utc": now = now.ToOffset(TimeSpan.FromHours(7)); break;
            default: throw new InvalidOperationException("Unknown test fault.");
        }
        AssertInvalidationProofRefused(() => GroupOriginalInvalidationProof.Require(input.Row, scope, batch,
            original, input.Claim, now));
        Assert.Equal(keys, f.Keys.Reads); Assert.False(f.Auth.Db.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData("plan", "null")]
    [InlineData("plan", "short")]
    [InlineData("plan", "lower")]
    [InlineData("plan", "nonhex")]
    [InlineData("observed", "null")]
    [InlineData("observed", "short")]
    [InlineData("observed", "lower")]
    [InlineData("observed", "nonhex")]
    [InlineData("checkpoint", "null")]
    [InlineData("checkpoint", "short")]
    [InlineData("checkpoint", "lower")]
    [InlineData("checkpoint", "nonhex")]
    [InlineData("checkpoint", "different")]
    public async Task OriginalInvalidationProofRequiresAllThreeCanonicalHashes(string field, string shape)
    {
        using var f = new Fixture(); var input = await InvalidationProofInputAsync(f);
        var row = CopyInvalidationRow(input.Row);
        var value = shape switch
        {
            "null" => null,
            "short" => new string('A', 63),
            "lower" => new string('a', 64),
            "different" => new string('B', 64),
            _ => new string('G', 64)
        };
        switch (field)
        {
            case "plan": row.PlanSha256 = value!; break;
            case "observed": row.ObservedDependenciesSha256 = value!; break;
            case "checkpoint": row.CheckpointSha256 = value!; break;
        }
        if (field != "checkpoint") row.CheckpointSha256 = GroupOriginalInvalidationProof.Digest(row);
        AssertInvalidationProofRefused(() => GroupOriginalInvalidationProof.Require(row, input.Handle.Receipt.Scope,
            row.BatchId, input.Original, input.Claim, input.Now));
    }

    [Fact]
    public async Task OriginalInvalidationProofDetectsChangedObservationDigestAndKeepsDiagnosticsFixed()
    {
        using var f = new Fixture(); var input = await InvalidationProofInputAsync(f);
        var row = CopyInvalidationRow(input.Row); row.ObservedDependenciesSha256 = new string('C', 64);
        Assert.NotEqual(input.Row.CheckpointSha256, GroupOriginalInvalidationProof.Digest(row));
        AssertInvalidationProofRefused(() => GroupOriginalInvalidationProof.Require(row, input.Handle.Receipt.Scope,
            row.BatchId, input.Original, input.Claim, input.Now));
        Assert.Equal("Group original mismatch checkpoint (no continuation authority).", row.ToString());
        Assert.Equal("Authenticated original metadata (private, no authority).", input.Original.ToString());
        Assert.DoesNotContain(row.ObservedDependenciesSha256, row.ToString());
        Assert.DoesNotContain(input.Original.Plan.Sha256, input.Original.ToString());
        Assert.DoesNotContain("Tồn kho", row.ToString());
        // A self-digest is corruption detection, not independent authentication of
        // a resealed observation. The owner must obtain and recheck that observation.
    }

    [Fact]
    public async Task OriginalInvalidationProofNullInputsRefuseWithoutNewKeysOrEffects()
    {
        using var f = new Fixture(); var input = await InvalidationProofInputAsync(f);
        var before = await InvalidationEffectsAsync(f); var keys = f.Keys.Reads;
        Assert.Throws<ArgumentNullException>(() => GroupOriginalInvalidationProof.Require(null!, input.Handle.Receipt.Scope,
            input.Row.BatchId, input.Original, input.Claim, input.Now));
        Assert.Throws<ArgumentNullException>(() => GroupOriginalInvalidationProof.Require(input.Row, input.Handle.Receipt.Scope,
            input.Row.BatchId, null!, input.Claim, input.Now));
        Assert.Throws<ArgumentNullException>(() => GroupOriginalInvalidationProof.Require(input.Row, input.Handle.Receipt.Scope,
            input.Row.BatchId, input.Original, null!, input.Now));
        Assert.Equal(before, await InvalidationEffectsAsync(f)); Assert.Equal(keys, f.Keys.Reads);
    }

    [Fact]
    public async Task OriginalInvalidationCheckpointNullCancellationAndInMemoryRefuseBeforeNewKeysOrEffects()
    {
        using var f = new Fixture(); var input = await InvalidationProofInputAsync(f);
        var store = new GroupOriginalInvalidationStore(f.Auth.Db, f.Worker, f.Auth.Clock, f.Reader, BrainReader(f));
        var before = await InvalidationEffectsAsync(f); var keys = f.Keys.Reads;
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.CheckpointAsync(null!));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CheckpointAsync(input.Handle, canceled.Token));
        Assert.Equal(canceled.Token, error.CancellationToken);
        var unavailable = await Assert.ThrowsAsync<InvalidOperationException>(() => store.CheckpointAsync(input.Handle));
        Assert.Equal("Group original invalidation checkpoint is unavailable.", unavailable.Message);
        Assert.Null(unavailable.InnerException);
        Assert.Equal(before, await InvalidationEffectsAsync(f)); Assert.Equal(keys, f.Keys.Reads);
        Assert.False(f.Auth.Db.ChangeTracker.HasChanges()); Assert.Null(f.Auth.Db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("company")]
    [InlineData("service")]
    [InlineData("credential")]
    public async Task OriginalInvalidationCheckpointForeignWorkerRefusesBeforeConnectionKeysAndEffects(string field)
    {
        using var f = new Fixture(); var input = await InvalidationProofInputAsync(f);
        var worker = field switch
        {
            "tenant" => f.Worker with { TenantId = Guid.NewGuid() },
            "company" => f.Worker with { CompanyId = Guid.NewGuid() },
            "service" => f.Worker with { ServiceId = Guid.NewGuid() },
            _ => f.Worker with { CredentialEpoch = f.Worker.CredentialEpoch + 1 }
        };
        var trap = new ModelFenceConnectionTrap(); var keys = f.Keys.Reads; var before = await InvalidationEffectsAsync(f);
        using var database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer("Server=owned.invalid;Initial Catalog=Owned;Integrated Security=true;Pooling=false")
            .AddInterceptors(trap).Options);
        var sources = new GroupBatchSourceReader(database, worker, f.Auth.Clock, f.Keys, new());
        var brain = new GroupBrainCurrentReader(database, worker, f.Auth.Clock, f.Keys, new());
        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new GroupOriginalInvalidationStore(database, worker, f.Auth.Clock, sources, brain).CheckpointAsync(input.Handle));
        Assert.Equal("Group service access is not available.", error.Message); Assert.Null(error.InnerException);
        Assert.Equal(0, trap.Attempts); Assert.Equal(keys, f.Keys.Reads);
        Assert.Equal(before, await InvalidationEffectsAsync(f));
        Assert.Equal(ConnectionState.Closed, database.Database.GetDbConnection().State);
        Assert.Null(database.Database.CurrentTransaction); Assert.Empty(database.ChangeTracker.Entries());
    }

    private sealed record InvalidationProofInput(GroupAuthenticatedOriginalMetadata Original,
        GroupBatchClaimHandle Handle, GroupBatchClaimReceiptRecord Claim, GroupOriginalInvalidationRecord Row,
        DateTimeOffset Now, IReadOnlyList<GroupAutomaticInputPlan.RestoredChunk> Chunks);

    private static async Task<InvalidationProofInput> InvalidationProofInputAsync(Fixture f)
    {
        var (plan, chunks) = await OriginalAttemptInputAsync(f);
        var handle = chunks[0].Brain.Handle;
        var claim = await f.Auth.Db.GroupBatchClaimReceipts.AsNoTracking().SingleAsync(x =>
            x.TenantId == handle.Receipt.Scope.TenantId && x.CompanyId == handle.Receipt.Scope.CompanyId
            && x.BindingId == handle.Receipt.Scope.SourceBindingId && x.BatchId == handle.Receipt.BatchId
            && x.OperationId == handle.Receipt.OperationId);
        var original = new GroupAuthenticatedOriginalMetadata(plan, claim.OperationId, claim.Epoch, claim.IssuedAtUtc);
        var row = new GroupOriginalInvalidationRecord
        {
            TenantId = claim.TenantId,
            CompanyId = claim.CompanyId,
            BindingId = claim.BindingId,
            BatchId = claim.BatchId,
            FormatVersion = 1,
            PlanSha256 = plan.Sha256,
            OriginalClaimOperationId = claim.OperationId,
            OriginalClaimEpoch = claim.Epoch,
            ClaimOperationId = claim.OperationId,
            ClaimEpoch = claim.Epoch,
            ObservedDependenciesSha256 = new string('A', 64),
            CreatedAtUtc = claim.IssuedAtUtc.AddSeconds(1)
        };
        row.CheckpointSha256 = GroupOriginalInvalidationProof.Digest(row);
        return new(original, handle, claim, row, row.CreatedAtUtc.AddSeconds(1), chunks);
    }

    private static void RequireInvalidationProof(InvalidationProofInput input) => GroupOriginalInvalidationProof.Require(
        input.Row, input.Handle.Receipt.Scope, input.Row.BatchId, input.Original, input.Claim, input.Now);

    private static void AssertInvalidationProofRefused(Action action)
    {
        var error = Assert.Throws<InvalidOperationException>(action);
        Assert.Equal("Group original invalidation proof is unavailable.", error.Message); Assert.Null(error.InnerException);
    }

    private static GroupOriginalInvalidationRecord CopyInvalidationRow(GroupOriginalInvalidationRecord row) => new()
    {
        TenantId = row.TenantId,
        CompanyId = row.CompanyId,
        BindingId = row.BindingId,
        BatchId = row.BatchId,
        FormatVersion = row.FormatVersion,
        PlanSha256 = row.PlanSha256,
        OriginalClaimOperationId = row.OriginalClaimOperationId,
        OriginalClaimEpoch = row.OriginalClaimEpoch,
        ClaimOperationId = row.ClaimOperationId,
        ClaimEpoch = row.ClaimEpoch,
        ObservedDependenciesSha256 = row.ObservedDependenciesSha256,
        CheckpointSha256 = row.CheckpointSha256,
        CreatedAtUtc = row.CreatedAtUtc
    };

    private static GroupBatchClaimReceiptRecord CopyInvalidationClaim(GroupBatchClaimReceiptRecord row) => new()
    {
        TenantId = row.TenantId,
        CompanyId = row.CompanyId,
        BindingId = row.BindingId,
        BatchId = row.BatchId,
        OperationId = row.OperationId,
        OwnerId = row.OwnerId,
        Epoch = row.Epoch,
        RequestedLifetimeTicks = row.RequestedLifetimeTicks,
        IssuedAtUtc = row.IssuedAtUtc,
        ExpiresAtUtc = row.ExpiresAtUtc,
        ServiceId = row.ServiceId,
        CredentialEpoch = row.CredentialEpoch,
        GrantVersion = row.GrantVersion,
        SourceVersion = row.SourceVersion,
        DeletionGeneration = row.DeletionGeneration,
        AccountVersion = row.AccountVersion,
        AuthoritySha256 = row.AuthoritySha256
    };

    private static async Task<string> InvalidationEffectsAsync(Fixture f) => string.Join("/", await f.CountsAsync(),
        await f.Auth.Db.GroupOriginalInvalidations.CountAsync(), await f.Auth.Db.GroupAutomaticInputPlans.CountAsync(),
        await f.Auth.Db.GroupModelAttemptStarts.CountAsync(), await f.Auth.Db.GroupModelAttemptFailures.CountAsync(),
        await f.Auth.Db.GroupModelAttemptResults.CountAsync(), await f.Auth.Db.GroupWorkCommitReceipts.CountAsync(),
        await f.Auth.Db.GroupCustomerRequests.CountAsync(), await f.Auth.Db.GroupNotesCommittedOutbox.CountAsync(),
        await f.Auth.Db.GroupBatchTerminalReceipts.CountAsync());
}
