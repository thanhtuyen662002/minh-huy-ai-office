using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupBatchClaimStoreTests
{
    [Fact]
    public async Task ExpiredNonceRemainsMetadataAcrossClockRollbackAndFirstWitnessNeverMovesWithinEpoch()
    {
        using var fixture = await Fixture.CreateAsync(); var owner = Guid.NewGuid(); var operation = Guid.NewGuid();
        var first = await fixture.AcquireAsync(owner, operation);
        var witnessedAt = first.Receipt.ExpiresAtUtc.AddSeconds(5);
        fixture.Auth.Clock.Current = witnessedAt;
        using (var db = new PlatformDbContext(fixture.Auth.Options))
            Assert.Null((await fixture.StoreFor(db).TryAcquireAsync(fixture.Scope, fixture.Batch, owner, operation, Fixture.Lifetime))!.CurrentHandle);
        Assert.Equal(witnessedAt, (await fixture.StateAsync()).ExpiryObservedAtUtc);
        foreach (var at in new[] { first.Receipt.ExpiresAtUtc.AddTicks(-1), first.Receipt.ExpiresAtUtc, witnessedAt.AddTicks(-1) })
        {
            fixture.Auth.Clock.Current = at;
            using var db = new PlatformDbContext(fixture.Auth.Options);
            var replay = await fixture.StoreFor(db).TryAcquireAsync(fixture.Scope, fixture.Batch, owner, operation, Fixture.Lifetime);
            Assert.Equal(first.Receipt, replay!.Receipt); Assert.Null(replay.CurrentHandle);
            Assert.Null(await fixture.StoreFor(db).TryAcquireAsync(fixture.Scope, fixture.Batch, owner, Guid.NewGuid(), Fixture.Lifetime));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.StoreFor(db).RequireCurrentAsync(first.CurrentHandle!));
        }
        fixture.Auth.Clock.Current = witnessedAt.AddSeconds(10);
        await fixture.AcquireAsync(owner, operation);
        Assert.Equal(witnessedAt, (await fixture.StateAsync()).ExpiryObservedAtUtc);
        fixture.Auth.Clock.Current = witnessedAt;
        var second = await fixture.AcquireAsync(owner, Guid.NewGuid());
        Assert.Equal(2, second.Receipt.Epoch); Assert.NotNull(second.CurrentHandle);
        Assert.Null((await fixture.StateAsync()).ExpiryObservedAtUtc);
        Assert.Null((await fixture.AcquireAsync(owner, operation)).CurrentHandle);
        Assert.Equal(2, await fixture.Auth.Db.GroupBatchClaimReceipts.CountAsync());
    }

    [Fact]
    public async Task DeniedPublicHandleCommitsExpiryWitnessBeforeCrossCallClockRollback()
    {
        using var fixture = await Fixture.CreateAsync(); var first = await fixture.AcquireAsync(Guid.NewGuid(), Guid.NewGuid());
        fixture.Auth.Clock.Current = first.Receipt.ExpiresAtUtc;
        using (var db = new PlatformDbContext(fixture.Auth.Options))
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.StoreFor(db).RequireCurrentAsync(first.CurrentHandle!));
        Assert.Equal(first.Receipt.ExpiresAtUtc, (await fixture.StateAsync()).ExpiryObservedAtUtc);
        fixture.Auth.Clock.Current = first.Receipt.ExpiresAtUtc.AddTicks(-1);
        using var restarted = new PlatformDbContext(fixture.Auth.Options);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.StoreFor(restarted).RequireCurrentAsync(first.CurrentHandle!));
        Assert.Equal(1, await restarted.GroupBatchClaimReceipts.CountAsync()); Assert.False(restarted.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalAwaitExpiryOrClockRollbackPreservesTheFirstExpiryObservation(bool rollback)
    {
        using var fixture = await Fixture.CreateAsync(); var first = await fixture.AcquireAsync(Guid.NewGuid(), Guid.NewGuid());
        var expiry = first.Receipt.ExpiresAtUtc;
        var clock = new SequenceClock(rollback ? [expiry, expiry.AddTicks(-1)] : [expiry.AddTicks(-1), expiry]);
        using var db = new PlatformDbContext(fixture.Auth.Options);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new GroupBatchClaimStore(db, fixture.Worker, clock)
            .RequireCurrentAsync(first.CurrentHandle!));
        Assert.Equal(expiry, (await fixture.StateAsync()).ExpiryObservedAtUtc);
        fixture.Auth.Clock.Current = expiry.AddTicks(-1);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Store.RequireCurrentAsync(first.CurrentHandle!));
    }

    [Fact]
    public async Task ExpiredLockedVerdictHasNoAllocationAndOnlyExplicitRetirementWritesWitness()
    {
        using var fixture = await Fixture.CreateAsync(); var first = await fixture.AcquireAsync(Guid.NewGuid(), Guid.NewGuid());
        fixture.Auth.Clock.Current = first.Receipt.ExpiresAtUtc;
        var expired = Assert.IsType<GroupBatchClaimFenceVerdict.Expired>(
            await fixture.Store.InspectCurrentLockedAsync(first.CurrentHandle!, default));
        Assert.DoesNotContain(expired.GetType().GetProperties(), x => x.PropertyType == typeof(GroupBatchAllocationReceipt));
        Assert.Null((await fixture.StateAsync()).ExpiryObservedAtUtc); Assert.False(fixture.Auth.Db.ChangeTracker.HasChanges());
        fixture.Auth.Clock.Current -= TimeSpan.FromTicks(1);
        await fixture.Store.RetireExpiredLockedAsync(expired.Observation, default);
        Assert.Equal(first.Receipt.ExpiresAtUtc, (await fixture.StateAsync()).ExpiryObservedAtUtc);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Store.RequireCurrentAsync(first.CurrentHandle!));
    }

    [Fact]
    public async Task OldExpiryObservationCannotRetireANewEpoch()
    {
        using var fixture = await Fixture.CreateAsync(); var first = await fixture.AcquireAsync(Guid.NewGuid(), Guid.NewGuid());
        fixture.Auth.Clock.Current = first.Receipt.ExpiresAtUtc;
        var expired = Assert.IsType<GroupBatchClaimFenceVerdict.Expired>(
            await fixture.Store.InspectCurrentLockedAsync(first.CurrentHandle!, default));
        var second = await fixture.AcquireAsync(Guid.NewGuid(), Guid.NewGuid());
        using var db = new PlatformDbContext(fixture.Auth.Options);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.StoreFor(db).RetireExpiredLockedAsync(expired.Observation, default));
        Assert.Null((await fixture.StateAsync()).ExpiryObservedAtUtc);
        await fixture.Store.RequireCurrentAsync(second.CurrentHandle!);
        fixture.Auth.Clock.Current = second.Receipt.ExpiresAtUtc;
        var old = await fixture.AcquireAsync(first.Receipt.OwnerId, first.Receipt.OperationId);
        Assert.Equal(first.Receipt, old.Receipt); Assert.Null(old.CurrentHandle);
        Assert.Null((await fixture.StateAsync()).ExpiryObservedAtUtc);
    }

    [Fact]
    public async Task RetirementRefusesStagedWritesAndCurrentRevocation()
    {
        using var fixture = await Fixture.CreateAsync(); var first = await fixture.AcquireAsync(Guid.NewGuid(), Guid.NewGuid());
        fixture.Auth.Clock.Current = first.Receipt.ExpiresAtUtc;
        var expired = Assert.IsType<GroupBatchClaimFenceVerdict.Expired>(
            await fixture.Store.InspectCurrentLockedAsync(first.CurrentHandle!, default));
        var state = await fixture.Auth.Db.GroupBatchClaimStates.SingleAsync(); state.OwnerId = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.RetireExpiredLockedAsync(expired.Observation, default));
        fixture.Auth.Db.ChangeTracker.Clear();
        Assert.Null((await fixture.StateAsync()).ExpiryObservedAtUtc);
        await fixture.ChangeAuthorityAsync("grant");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Store.RetireExpiredLockedAsync(expired.Observation, default));
        Assert.Null((await fixture.StateAsync()).ExpiryObservedAtUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedStoredExpiryWitnessCannotRestoreOrReplaceAClaim(bool nonUtc)
    {
        using var fixture = await Fixture.CreateAsync(); var first = await fixture.AcquireAsync(Guid.NewGuid(), Guid.NewGuid());
        var state = await fixture.Auth.Db.GroupBatchClaimStates.SingleAsync();
        state.ExpiryObservedAtUtc = nonUtc ? first.Receipt.ExpiresAtUtc.ToOffset(TimeSpan.FromHours(1)) : first.Receipt.ExpiresAtUtc.AddTicks(-1);
        await fixture.Auth.Db.SaveChangesAsync(); fixture.Auth.Db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.RequireCurrentAsync(first.CurrentHandle!));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.AcquireAsync(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public async Task ActiveLeaseCannotBeStolenAndOriginalNonceNeverRenewsAcrossFreshContexts()
    {
        using var fixture = await Fixture.CreateAsync(); var owner = Guid.NewGuid(); var operation = Guid.NewGuid();
        var original = await fixture.AcquireAsync(owner, operation);
        Assert.NotNull(original.CurrentHandle); Assert.False(original.WasAlreadyClaimed); Assert.Equal(1, original.Receipt.Epoch);
        fixture.Auth.Clock.Current += TimeSpan.FromSeconds(5);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var db = new PlatformDbContext(fixture.Auth.Options);
            var replay = await fixture.StoreFor(db).TryAcquireAsync(fixture.Scope, fixture.Batch, owner, operation, Fixture.Lifetime);
            Assert.True(replay!.WasAlreadyClaimed); Assert.Equal(original.Receipt, replay.Receipt); Assert.NotNull(replay.CurrentHandle);
            Assert.Null(await fixture.StoreFor(db).TryAcquireAsync(fixture.Scope, fixture.Batch, owner, Guid.NewGuid(), Fixture.Lifetime));
        }
        Assert.Equal(1, await fixture.Auth.Db.GroupBatchClaimReceipts.CountAsync());
        Assert.Equal(original.Receipt.ExpiresAtUtc, (await fixture.StateAsync()).ExpiresAtUtc);
        await fixture.Store.RequireCurrentAsync(original.CurrentHandle!);
    }

    [Fact]
    public async Task ExpiryProducesMonotoneReplacementAndPermanentlyFencesOriginalHandle()
    {
        using var fixture = await Fixture.CreateAsync(); var owner = Guid.NewGuid(); var operation = Guid.NewGuid();
        var first = await fixture.AcquireAsync(owner, operation);
        fixture.Auth.Clock.Current = first.Receipt.ExpiresAtUtc;
        var expired = await fixture.AcquireAsync(owner, operation);
        Assert.Equal(first.Receipt, expired.Receipt); Assert.Null(expired.CurrentHandle);
        Assert.Equal(1, (await fixture.StateAsync()).Epoch);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Store.RequireCurrentAsync(first.CurrentHandle!));
        var second = await fixture.AcquireAsync(Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(2, second.Receipt.Epoch); Assert.NotNull(second.CurrentHandle);
        var oldReplay = await fixture.AcquireAsync(owner, operation);
        Assert.Equal(first.Receipt, oldReplay.Receipt); Assert.Null(oldReplay.CurrentHandle);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Store.RequireCurrentAsync(first.CurrentHandle!));
        await fixture.Store.RequireCurrentAsync(second.CurrentHandle!);
        Assert.Equal(2, await fixture.Auth.Db.GroupBatchClaimReceipts.CountAsync());
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("batch")]
    [InlineData("lifetime")]
    public async Task OriginalNonceConflictingIntentIsRefusedWithoutRenewal(string changed)
    {
        using var fixture = await Fixture.CreateAsync(); var owner = Guid.NewGuid(); var operation = Guid.NewGuid();
        var first = await fixture.AcquireAsync(owner, operation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.TryAcquireAsync(fixture.Scope,
            changed == "batch" ? Guid.NewGuid() : fixture.Batch, changed == "owner" ? Guid.NewGuid() : owner,
            operation, changed == "lifetime" ? Fixture.Lifetime + TimeSpan.FromSeconds(1) : Fixture.Lifetime));
        Assert.Equal(first.Receipt.ExpiresAtUtc, (await fixture.StateAsync()).ExpiresAtUtc);
        Assert.Equal(1, await fixture.Auth.Db.GroupBatchClaimReceipts.CountAsync());
    }

    [Theory]
    [InlineData("grant-version")]
    [InlineData("source-version")]
    [InlineData("deletion-generation")]
    [InlineData("account-version")]
    [InlineData("credential-reference")]
    [InlineData("display-name")]
    [InlineData("opaque-group")]
    public async Task ExactAuthorityChangePreservesOriginalMetadataButNeverRestoresOldHandle(string changed)
    {
        using var fixture = await Fixture.CreateAsync(); var owner = Guid.NewGuid(); var operation = Guid.NewGuid();
        var first = await fixture.AcquireAsync(owner, operation);
        await fixture.ChangeAuthorityAsync(changed);
        var replay = await fixture.AcquireAsync(owner, operation);
        Assert.Equal(first.Receipt, replay.Receipt); Assert.Null(replay.CurrentHandle);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Store.RequireCurrentAsync(first.CurrentHandle!));
        fixture.Auth.Clock.Current = first.Receipt.ExpiresAtUtc;
        var replacement = await fixture.AcquireAsync(Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(2, replacement.Receipt.Epoch); Assert.NotNull(replacement.CurrentHandle);
        await fixture.Store.RequireCurrentAsync(replacement.CurrentHandle!);
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("source")]
    [InlineData("account")]
    [InlineData("company")]
    [InlineData("service")]
    [InlineData("epoch")]
    [InlineData("role")]
    public async Task RevocationDeniesBothFreshAcquisitionAndOriginalReconciliation(string changed)
    {
        using var fixture = await Fixture.CreateAsync(); var owner = Guid.NewGuid(); var operation = Guid.NewGuid();
        var first = await fixture.AcquireAsync(owner, operation); await fixture.ChangeAuthorityAsync(changed);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.AcquireAsync(owner, operation));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.AcquireAsync(Guid.NewGuid(), Guid.NewGuid()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Store.RequireCurrentAsync(first.CurrentHandle!));
        Assert.Equal(1, await fixture.Auth.Db.GroupBatchClaimReceipts.CountAsync());
    }

    [Theory]
    [InlineData("state-owner")]
    [InlineData("state-operation")]
    [InlineData("state-epoch")]
    [InlineData("state-expiry")]
    [InlineData("missing-state")]
    [InlineData("receipt-expiry")]
    [InlineData("receipt-hash")]
    [InlineData("receipt-scope")]
    [InlineData("missing-receipt")]
    [InlineData("allocation-ledger")]
    public async Task CorruptOriginalGraphCannotBecomeANewSuccessfulLease(string changed)
    {
        using var fixture = await Fixture.CreateAsync(); var owner = Guid.NewGuid(); var operation = Guid.NewGuid();
        await fixture.AcquireAsync(owner, operation);
        var state = await fixture.Auth.Db.GroupBatchClaimStates.SingleAsync();
        var receipt = await fixture.Auth.Db.GroupBatchClaimReceipts.SingleAsync();
        switch (changed)
        {
            case "state-owner": state.OwnerId = Guid.NewGuid(); break;
            case "state-operation": state.OperationId = Guid.NewGuid(); break;
            case "state-epoch": state.Epoch++; break;
            case "state-expiry": state.ExpiresAtUtc += TimeSpan.FromSeconds(1); break;
            case "missing-state": fixture.Auth.Db.Remove(state); break;
            case "receipt-expiry": receipt.ExpiresAtUtc += TimeSpan.FromSeconds(1); break;
            case "receipt-hash": receipt.AuthoritySha256 = new string('x', 64); break;
            case "receipt-scope":
                fixture.Auth.Db.Remove(receipt); await fixture.Auth.Db.SaveChangesAsync();
                fixture.Auth.Db.ChangeTracker.Clear(); receipt.BindingId = Guid.NewGuid(); fixture.Auth.Db.Add(receipt); break;
            case "missing-receipt": fixture.Auth.Db.Remove(receipt); break;
            case "allocation-ledger": (await fixture.Auth.Db.GroupBatchAllocatedRevisions.SingleAsync()).ContentSha256 = new string('C', 64); break;
        }
        await fixture.Auth.Db.SaveChangesAsync(); fixture.Auth.Db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.AcquireAsync(owner, operation));
        Assert.Equal(1, await fixture.Auth.Db.GroupBatchAllocations.CountAsync());
        Assert.True(await fixture.Auth.Db.GroupBatchClaimReceipts.CountAsync() <= 1);
    }

    [Fact]
    public async Task UnrelatedIngressCursorAdvanceDoesNotInvalidateOriginalClaim()
    {
        using var fixture = await Fixture.CreateAsync(); var first = await fixture.AcquireAsync(Guid.NewGuid(), Guid.NewGuid());
        var source = await fixture.Auth.Db.GroupSourceStates.SingleAsync(); source.CommittedSequence++;
        source.FirstPendingAtUtc = fixture.Auth.Clock.Current; source.LastPendingAtUtc = fixture.Auth.Clock.Current;
        await fixture.Auth.Db.SaveChangesAsync();
        await fixture.Store.RequireCurrentAsync(first.CurrentHandle!);
    }

    [Fact]
    public async Task LateAuthorityRevocationBeforeSaveDoesNotWriteAClaim()
    {
        using var fixture = await Fixture.CreateAsync();
        var clock = new ChangingClock(fixture.Auth.Clock.Current, () => fixture.ChangeAuthorityAsync("grant-version").GetAwaiter().GetResult());
        var store = new GroupBatchClaimStore(fixture.Auth.Db, fixture.Worker, clock);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.TryAcquireAsync(fixture.Scope, fixture.Batch,
            Guid.NewGuid(), Guid.NewGuid(), Fixture.Lifetime));
        Assert.Equal(0, await fixture.Auth.Db.GroupBatchClaimReceipts.CountAsync());
        Assert.Equal(0, await fixture.Auth.Db.GroupBatchClaimStates.CountAsync());
    }

    [Fact]
    public async Task ExpiryDuringFinalProofAndNonUtcOrBackwardClockAreRefusedBeforeSave()
    {
        using var fixture = await Fixture.CreateAsync(); var now = fixture.Auth.Clock.Current;
        foreach (var times in new[] { new[] { now, now + Fixture.Lifetime }, new[] { now, now.AddTicks(-1) },
            new[] { now.ToOffset(TimeSpan.FromHours(1)) } })
        {
            var store = new GroupBatchClaimStore(fixture.Auth.Db, fixture.Worker, new SequenceClock(times));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.TryAcquireAsync(fixture.Scope, fixture.Batch,
                Guid.NewGuid(), Guid.NewGuid(), Fixture.Lifetime));
            Assert.Equal(0, await fixture.Auth.Db.GroupBatchClaimReceipts.CountAsync());
        }
    }

    [Fact]
    public async Task ScopeCancellationAndRequestBoundsFailBeforeOpeningDisposedDatabase()
    {
        using var fixture = await Fixture.CreateAsync(); fixture.Auth.Db.Dispose();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Store.TryAcquireAsync(fixture.Scope with { CompanyId = Guid.NewGuid() },
            fixture.Batch, Guid.NewGuid(), Guid.NewGuid(), Fixture.Lifetime));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Store.TryAcquireAsync(fixture.Scope, fixture.Batch,
            Guid.NewGuid(), Guid.NewGuid(), Fixture.Lifetime, canceled.Token));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(601)]
    public async Task LeaseBoundsAndEmptyIdsAreRefusedWithoutEffects(int seconds)
    {
        using var fixture = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.TryAcquireAsync(fixture.Scope, fixture.Batch,
            Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromSeconds(seconds)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.TryAcquireAsync(fixture.Scope, Guid.Empty,
            Guid.NewGuid(), Guid.NewGuid(), Fixture.Lifetime));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.TryAcquireAsync(fixture.Scope, fixture.Batch,
            Guid.Empty, Guid.NewGuid(), Fixture.Lifetime));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.TryAcquireAsync(fixture.Scope, fixture.Batch,
            Guid.NewGuid(), Guid.Empty, Fixture.Lifetime));
        Assert.Equal(0, await fixture.Auth.Db.GroupBatchClaimReceipts.CountAsync());
    }

    [Fact]
    public void ModelHasScopedImmutableReceiptsAndOnlyMetadataLeaseState()
    {
        using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer("Server=localhost;Database=Owned_Model;User Id=model;Password=model").Options);
        var model = db.GetService<IDesignTimeModel>().Model;
        foreach (var type in new[] { typeof(GroupBatchClaimStateRecord), typeof(GroupBatchClaimReceiptRecord) })
        {
            var entity = model.FindEntityType(type)!;
            Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId" }, entity.FindPrimaryKey()!.Properties.Take(3).Select(x => x.Name));
            foreach (var fk in entity.GetForeignKeys()) Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior);
            foreach (var forbidden in new[] { "ProtectedContent", "SecretReference", "NotesJson", "Completed", "UserId", "TaskId" }) Assert.Null(entity.FindProperty(forbidden));
        }
        var receipt = model.FindEntityType(typeof(GroupBatchClaimReceiptRecord))!;
        Assert.Contains(receipt.GetIndexes(), x => x.IsUnique && x.Properties.Select(p => p.Name)
            .SequenceEqual(new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "Epoch" }));
        Assert.Equal("Latin1_General_100_BIN2", receipt.FindProperty("AuthoritySha256")!.GetCollation());
        Assert.Empty(typeof(GroupBatchClaimHandle).GetConstructors());
    }

    private sealed class SequenceClock(DateTimeOffset[] times) : TimeProvider
    {
        private int index;
        public override DateTimeOffset GetUtcNow() => times[Math.Min(index++, times.Length - 1)];
    }
    private sealed class ChangingClock(DateTimeOffset now, Action change) : TimeProvider
    {
        private bool changed;
        public override DateTimeOffset GetUtcNow() { if (!changed) { changed = true; change(); } return now; }
    }
    private sealed class Fixture : IDisposable
    {
        internal static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);
        internal readonly GroupServiceAuthenticatorTests.Fixture Auth = new("capability");
        internal GroupScope Scope => Auth.Scope;
        internal Guid Batch;
        internal GroupExtractionWorkerBinding Worker => new(Scope.TenantId, Scope.CompanyId, Auth.Service.Id, 1);
        internal GroupBatchClaimStore Store => StoreFor(Auth.Db);
        internal GroupBatchClaimStore StoreFor(PlatformDbContext db) => new(db, Worker, Auth.Clock);
        internal Task<GroupBatchClaimStateRecord> StateAsync() => Auth.Db.GroupBatchClaimStates.AsNoTracking().SingleAsync();
        internal async Task<GroupBatchClaimResult> AcquireAsync(Guid owner, Guid operation) =>
            (await Store.TryAcquireAsync(Scope, Batch, owner, operation, Lifetime))!;
        internal static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture(); var message = Guid.NewGuid(); var at = f.Auth.Clock.Current.AddSeconds(-120);
            f.Auth.Db.Add(new GroupMessageRecord
            {
                TenantId = f.Scope.TenantId,
                CompanyId = f.Scope.CompanyId,
                BindingId = f.Scope.SourceBindingId,
                Id = message,
                ExternalMessageId = "owned",
                IdentityHash = new string('A', 64)
            });
            f.Auth.Db.Add(new GroupMessageRevisionRecord
            {
                TenantId = f.Scope.TenantId,
                CompanyId = f.Scope.CompanyId,
                BindingId = f.Scope.SourceBindingId,
                MessageId = message,
                Revision = 1,
                CommittedSequence = 1,
                SourceVersion = 1,
                Kind = GroupSourceEventKind.NewText,
                ContentSha256 = new string('B', 64),
                ContentKeyId = "owned",
                ProtectedContent = new byte[29],
                ExternalRevisionEventId = "owned-event",
                SenderId = "owned-sender",
                OccurredAtUtc = at,
                CommittedAtUtc = at
            });
            f.Auth.Db.Add(new GroupIngressReceiptRecord
            {
                TenantId = f.Scope.TenantId,
                CompanyId = f.Scope.CompanyId,
                BindingId = f.Scope.SourceBindingId,
                EventIdentityHash = new string('A', 64),
                EnvelopeSha256 = new string('B', 64),
                ExternalRevisionEventId = "owned-event",
                MessageId = message,
                Revision = 1,
                ServiceId = f.Auth.Service.Id,
                CredentialEpoch = 1,
                ListenerEpoch = 1,
                CommittedAtUtc = at
            });
            f.Auth.Db.Add(new GroupSourceStateRecord
            {
                TenantId = f.Scope.TenantId,
                CompanyId = f.Scope.CompanyId,
                BindingId = f.Scope.SourceBindingId,
                CommittedSequence = 1,
                FirstPendingAtUtc = at,
                LastPendingAtUtc = at
            });
            await f.Auth.Db.SaveChangesAsync();
            f.Batch = (await new GroupBatchAllocationStore(f.Auth.Db, f.Worker, GroupBatchTiming.InitialTuning, f.Auth.Clock)
                .AllocateDueAsync(f.Scope, Guid.NewGuid()))!.BatchId;
            return f;
        }
        internal async Task ChangeAuthorityAsync(string changed)
        {
            using var db = new PlatformDbContext(Auth.Options);
            var binding = await db.GroupBindings.SingleAsync(); var account = await db.GroupConnectorAccounts.SingleAsync();
            var grant = await db.GroupServiceGrants.SingleAsync(); var service = await db.GroupServices.SingleAsync();
            switch (changed)
            {
                case "grant-version": grant.Version++; break;
                case "source-version": binding.Version++; break;
                case "deletion-generation": binding.DeletionGeneration++; break;
                case "account-version": account.Version++; break;
                case "credential-reference": service.CredentialReference = "secretref://env/OTHER_OWNED_KEY"; break;
                case "display-name": binding.DisplayName += "changed"; break;
                case "opaque-group":
                    binding.ExternalGroupId += " ";
                    binding.IdentityHash = new GroupExternalIdentity(binding.Provider, binding.ExternalAccountId, binding.ExternalGroupId).IndexKey();
                    binding.PhysicalGroupHash = GroupIngressIdentity.PhysicalGroupIndex(binding.Provider, binding.ExternalGroupId); break;
                case "grant": grant.IsEnabled = false; break;
                case "source": binding.IsEnabled = false; break;
                case "account": account.IsEnabled = false; break;
                case "company": (await db.Companies.SingleAsync()).IsActive = false; break;
                case "service": service.IsEnabled = false; break;
                case "epoch": service.CredentialEpoch++; break;
                case "role": binding.Role = GroupBindingRole.TechnicalInternal; break;
            }
            await db.SaveChangesAsync();
        }
        public void Dispose() => Auth.Dispose();
    }
}
