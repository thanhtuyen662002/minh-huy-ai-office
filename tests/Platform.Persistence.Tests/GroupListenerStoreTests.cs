using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupListenerStoreTests
{
    [Fact]
    public async Task FirstAcquireRecordsLeaseInterruptedCoverageAndCommandWithoutPortalOrSourceEffects()
    {
        using var fixture = new Fixture(); var receipt = await fixture.ApplyAsync();
        Assert.Equal(fixture.Account, receipt.Lease.Account); Assert.Equal(fixture.Owner, receipt.Lease.OwnerId);
        Assert.Equal(1, receipt.Lease.Epoch); Assert.Equal(Fixture.Now.AddSeconds(30), receipt.Lease.ExpiresAtUtc);
        Assert.True(receipt.Changed); Assert.True(receipt.CoverageRecorded); Assert.False(receipt.WasAlreadyCommitted);
        var gap = Assert.Single(await fixture.Auth.Db.GroupAccountCoverageGaps.AsNoTracking().ToArrayAsync());
        Assert.Equal("listener-started", gap.Reason); Assert.Equal(Fixture.Now, gap.OpenedAtUtc); Assert.Equal(Fixture.Now, gap.RecordedAtUtc);
        Assert.Single(await fixture.Auth.Db.GroupListenerCommandReceipts.ToArrayAsync());
        Assert.Empty(fixture.Auth.Db.GroupMessages); Assert.Empty(fixture.Auth.Db.GroupIngressReceipts); Assert.Empty(fixture.Auth.Db.GroupIngressOutbox);
        Assert.Empty(fixture.Auth.Db.Tasks); Assert.Empty(fixture.Auth.Db.CompanyMemberships);
    }

    [Fact]
    public async Task OneHundredSameNonceReplaysRecoverOriginalAckWithoutAnyExtensionOrAdditionalCoverage()
    {
        using var fixture = new Fixture(); var nonce = Guid.NewGuid(); var first = await fixture.ApplyAsync(nonce: nonce);
        for (var index = 0; index < 100; index++)
        {
            fixture.Auth.Clock.Current = Fixture.Now.AddMilliseconds(index + 1);
            Assert.Equal(first with { WasAlreadyCommitted = true }, await fixture.ApplyAsync(nonce: nonce));
        }
        Assert.Equal(first.Lease.ExpiresAtUtc, (await fixture.Auth.Db.GroupListenerLeases.AsNoTracking().SingleAsync()).ExpiresAtUtc);
        Assert.Single(fixture.Auth.Db.GroupAccountCoverageGaps); Assert.Single(fixture.Auth.Db.GroupListenerCommandReceipts);
    }

    [Fact]
    public async Task ExpiredSignedAcquireReplayCannotAdvanceEpochButFreshNonceCanAcquireNextEpoch()
    {
        using var fixture = new Fixture(); var nonce = Guid.NewGuid(); await fixture.ApplyAsync(nonce: nonce);
        fixture.Auth.Clock.Current = Fixture.Now.AddSeconds(31);
        // The old signature is still within120s authentication skew. Consuming
        // its already committed nonce must not create another lease.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.ApplyAsync(nonce: nonce, originalSigningTime: true));
        Assert.Equal(1, (await fixture.Auth.Db.GroupListenerLeases.AsNoTracking().SingleAsync()).Epoch);
        Assert.Single(fixture.Auth.Db.GroupAccountCoverageGaps); Assert.Single(fixture.Auth.Db.GroupListenerCommandReceipts);
        var second = await fixture.ApplyAsync();
        Assert.Equal(2, second.Lease.Epoch); Assert.Equal(Fixture.Now.AddSeconds(61), second.Lease.ExpiresAtUtc);
        var gaps = await fixture.Auth.Db.GroupAccountCoverageGaps.AsNoTracking().ToArrayAsync();
        Assert.Equal(2, gaps.Length); var interrupted = Assert.Single(gaps, x => x.ListenerEpoch == 2);
        Assert.Equal("listener-expired", interrupted.Reason); Assert.Equal(Fixture.Now, interrupted.OpenedAtUtc);
    }

    [Fact]
    public async Task StopAckReplayDoesNotReopenAccountAndOldAcquireOrRenewCannotResurrectIt()
    {
        using var fixture = new Fixture(); var acquireNonce = Guid.NewGuid(); await fixture.ApplyAsync(nonce: acquireNonce);
        fixture.Auth.Clock.Current = Fixture.Now.AddSeconds(5);
        var renewNonce = Guid.NewGuid(); await fixture.ApplyAsync(new(fixture.Owner, GroupListenerOperation.Renew, 1), renewNonce);
        fixture.Auth.Clock.Current = Fixture.Now.AddSeconds(10);
        var stop = new GroupListenerCommand(fixture.Owner, GroupListenerOperation.Stop, 1); var stopNonce = Guid.NewGuid();
        var stopped = await fixture.ApplyAsync(stop, stopNonce);
        fixture.Auth.Clock.Current = Fixture.Now.AddSeconds(90);
        Assert.Equal(stopped with { WasAlreadyCommitted = true }, await fixture.ApplyAsync(stop, stopNonce));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.ApplyAsync(nonce: acquireNonce));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.ApplyAsync(new(fixture.Owner, GroupListenerOperation.Renew, 1), renewNonce));
        var stored = await fixture.Auth.Db.GroupListenerLeases.AsNoTracking().SingleAsync();
        Assert.Equal(Fixture.Now.AddSeconds(10), stored.ExpiresAtUtc); Assert.Equal(stored.HeartbeatAtUtc, stored.ExpiresAtUtc);
        Assert.Equal(2, await fixture.Auth.Db.GroupAccountCoverageGaps.CountAsync()); Assert.Equal(3, await fixture.Auth.Db.GroupListenerCommandReceipts.CountAsync());
        var restarted = await fixture.ApplyAsync(new(Guid.NewGuid(), GroupListenerOperation.Acquire, 0)); Assert.Equal(2, restarted.Lease.Epoch);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.ApplyAsync(stop, stopNonce));
    }

    [Fact]
    public async Task LiveForeignOwnerAndStaleEpochCannotRenewStopOrAcquireAccount()
    {
        using var fixture = new Fixture(); await fixture.ApplyAsync();
        foreach (var command in new[] { new GroupListenerCommand(Guid.NewGuid(), GroupListenerOperation.Acquire, 0),
            new(fixture.Owner, GroupListenerOperation.Renew, 2), new(fixture.Owner, GroupListenerOperation.Stop, 2),
            new(Guid.NewGuid(), GroupListenerOperation.Renew, 1), new(Guid.NewGuid(), GroupListenerOperation.Stop, 1) })
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.ApplyAsync(command));
        Assert.Single(fixture.Auth.Db.GroupListenerCommandReceipts); Assert.Single(fixture.Auth.Db.GroupAccountCoverageGaps);
    }

    [Fact]
    public async Task SameNonceWithDifferentCommandRefusesConflictAndPreservesOriginalGraph()
    {
        using var fixture = new Fixture(); var nonce = Guid.NewGuid(); var first = await fixture.ApplyAsync(nonce: nonce);
        await Assert.ThrowsAsync<GroupListenerConflictException>(() => fixture.ApplyAsync(new(fixture.Owner, GroupListenerOperation.Stop, 1), nonce));
        Assert.Equal(first with { WasAlreadyCommitted = true }, await fixture.ApplyAsync(nonce: nonce));
        Assert.Single(fixture.Auth.Db.GroupListenerCommandReceipts); Assert.Single(fixture.Auth.Db.GroupAccountCoverageGaps);
    }

    [Fact]
    public async Task RenewalReplayReturnsOriginalDeadlineAfterLaterRenewalWithoutExtendingCurrentLease()
    {
        using var fixture = new Fixture(); await fixture.ApplyAsync();
        var renew = new GroupListenerCommand(fixture.Owner, GroupListenerOperation.Renew, 1); var nonce = Guid.NewGuid();
        fixture.Auth.Clock.Current = Fixture.Now.AddSeconds(5); var original = await fixture.ApplyAsync(renew, nonce);
        fixture.Auth.Clock.Current = Fixture.Now.AddSeconds(10); var later = await fixture.ApplyAsync(renew);
        fixture.Auth.Clock.Current = Fixture.Now.AddSeconds(12);
        Assert.Equal(original with { WasAlreadyCommitted = true }, await fixture.ApplyAsync(renew, nonce));
        Assert.Equal(later.Lease.ExpiresAtUtc, (await fixture.Auth.Db.GroupListenerLeases.AsNoTracking().SingleAsync()).ExpiresAtUtc);
        Assert.Equal(3, await fixture.Auth.Db.GroupListenerCommandReceipts.CountAsync()); Assert.Single(fixture.Auth.Db.GroupAccountCoverageGaps);
    }

    [Fact]
    public async Task ShortenedCurrentLeaseCannotReleaseAnOriginalAckWithLongerExpiry()
    {
        using var fixture = new Fixture(); var nonce = Guid.NewGuid(); var original = await fixture.ApplyAsync(nonce: nonce);
        var stored = await fixture.Auth.Db.GroupListenerLeases.SingleAsync();
        stored.ExpiresAtUtc = Fixture.Now.AddSeconds(10); await fixture.Auth.Db.SaveChangesAsync();
        fixture.Auth.Clock.Current = Fixture.Now.AddSeconds(1);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.ApplyAsync(nonce: nonce));
        Assert.Equal(Fixture.Now.AddSeconds(30), original.Lease.ExpiresAtUtc);
        Assert.Equal(Fixture.Now.AddSeconds(10), (await fixture.Auth.Db.GroupListenerLeases.AsNoTracking().SingleAsync()).ExpiresAtUtc);
        Assert.Single(fixture.Auth.Db.GroupListenerCommandReceipts); Assert.Single(fixture.Auth.Db.GroupAccountCoverageGaps);
    }

    [Fact]
    public async Task NewContextRecoversDurableReceiptAndRetainedEpochAfterProcessRestart()
    {
        using var fixture = new Fixture(); var nonce = Guid.NewGuid(); var first = await fixture.ApplyAsync(nonce: nonce);
        using var restarted = new PlatformDbContext(fixture.Auth.Options);
        var store = new GroupListenerStore(restarted, Fixture.Policy, fixture.Auth.Clock);
        Assert.Equal(first with { WasAlreadyCommitted = true }, await store.ApplyAsync(await fixture.VerifyAsync(nonce: nonce)));
        fixture.Auth.Clock.Current = Fixture.Now.AddSeconds(30);
        var next = await store.ApplyAsync(await fixture.VerifyAsync(new(Guid.NewGuid(), GroupListenerOperation.Acquire, 0)));
        Assert.Equal(2, next.Lease.Epoch); Assert.NotEqual(first.Lease.OwnerId, next.Lease.OwnerId);
        var old = await fixture.VerifyAsync(nonce: nonce);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.ApplyAsync(old));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("account")]
    [InlineData("grant")]
    [InlineData("epoch")]
    [InlineData("expired-signature")]
    public async Task CurrentAuthorityChangedAfterAuthenticationRefusesBeforeOwnershipWrite(string change)
    {
        using var fixture = new Fixture(); var verified = await fixture.VerifyAsync();
        if (change == "source") fixture.Auth.Binding.IsEnabled = false;
        if (change == "account") fixture.Auth.Account.IsEnabled = false;
        if (change == "grant") fixture.Auth.Grant.Version++;
        if (change == "epoch") fixture.Auth.Service.CredentialEpoch++;
        if (change == "expired-signature") fixture.Auth.Clock.Current = Fixture.Now.AddSeconds(121);
        await fixture.Auth.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Store.ApplyAsync(verified));
        Assert.Empty(fixture.Auth.Db.GroupListenerLeases); Assert.Empty(fixture.Auth.Db.GroupAccountCoverageGaps); Assert.Empty(fixture.Auth.Db.GroupListenerCommandReceipts);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task LeaseExpiryDuringEitherSqlWriteRefusesAckAndDetachesStagedEntities(int save)
    {
        using var fixture = new Fixture(); var verified = await fixture.VerifyAsync();
        using var delayed = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>(fixture.Auth.Options)
            .AddInterceptors(new AfterSave(save, () => fixture.Auth.Clock.Current = Fixture.Now.AddSeconds(31))).Options);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new GroupListenerStore(delayed, Fixture.Policy, fixture.Auth.Clock).ApplyAsync(verified));
        Assert.DoesNotContain(delayed.ChangeTracker.Entries(), x => x.Entity is GroupListenerLeaseRecord or GroupAccountCoverageGapRecord or GroupListenerCommandReceiptRecord);
        // EF InMemory has no transaction rollback. The native gate must prove
        // all-or-nothing persistence; this test proves no late live ACK escapes.
    }

    [Fact]
    public async Task FailedInitialSaveReturnsNoAckAndLeavesNoStagedTrackedObjects()
    {
        using var fixture = new Fixture(); var verified = await fixture.VerifyAsync();
        using var failing = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>(fixture.Auth.Options).AddInterceptors(new FailSave()).Options);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new GroupListenerStore(failing, Fixture.Policy, fixture.Auth.Clock).ApplyAsync(verified));
        Assert.Equal("Group listener ownership is unavailable.", error.Message);
        Assert.DoesNotContain(failing.ChangeTracker.Entries(), x => x.Entity is GroupListenerLeaseRecord or GroupAccountCoverageGapRecord or GroupListenerCommandReceiptRecord);
        Assert.Empty(fixture.Auth.Db.GroupListenerLeases); Assert.Empty(fixture.Auth.Db.GroupAccountCoverageGaps); Assert.Empty(fixture.Auth.Db.GroupListenerCommandReceipts);
    }

    [Fact]
    public async Task DirtyContextCannotAdmitListenerOrFlushUnrelatedWrites()
    {
        using var fixture = new Fixture(); var verified = await fixture.VerifyAsync(); fixture.Auth.Binding.DisplayName = "pending edit";
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.ApplyAsync(verified));
        Assert.Empty(fixture.Auth.Db.GroupListenerLeases); Assert.Empty(fixture.Auth.Db.GroupListenerCommandReceipts);
    }

    [Fact]
    public async Task CapturedNonceAndDigestRemainBoundToPrivateAuthenticatedBytes()
    {
        using var fixture = new Fixture(); var nonce = Guid.NewGuid(); var verified = await fixture.VerifyAsync(nonce: nonce);
        Assert.Equal(nonce, verified.Nonce);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(fixture.Body(new(fixture.Owner, GroupListenerOperation.Acquire, 0)))), verified.CommandSha256);
    }

    [Fact]
    public async Task ReusingServiceNonceForAnotherAuthorizedAccountCannotReconcileOrClaimThatAccount()
    {
        using var fixture = new Fixture(); var nonce = Guid.NewGuid(); await fixture.ApplyAsync(nonce: nonce);
        var external = new GroupExternalIdentity("synthetic", "another-account", "another-group");
        var account = new GroupConnectorAccountRecord
        {
            TenantId = fixture.Account.TenantId,
            CompanyId = fixture.Account.CompanyId,
            Id = Guid.NewGuid(),
            Provider = external.Provider,
            ExternalAccountId = external.AccountId,
            IdentityHash = GroupIngressIdentity.AccountIndex(external.Provider, external.AccountId),
            PackageVersion = fixture.Auth.Account.PackageVersion,
            GitCommit = fixture.Auth.Account.GitCommit,
            QualificationJson = fixture.Auth.Account.QualificationJson,
            IsEnabled = true
        };
        var binding = new GroupBindingRecord
        {
            TenantId = fixture.Account.TenantId,
            CompanyId = fixture.Account.CompanyId,
            Id = Guid.NewGuid(),
            ConnectorAccountId = account.Id,
            Provider = external.Provider,
            ExternalAccountId = external.AccountId,
            ExternalGroupId = external.GroupId,
            IdentityHash = external.IndexKey(),
            PhysicalGroupHash = GroupIngressIdentity.PhysicalGroupIndex(external.Provider, external.GroupId),
            DisplayName = "Second authorized source",
            Role = GroupBindingRole.CustomerSource,
            IsEnabled = true
        };
        fixture.Auth.Db.AddRange(account, binding, new GroupServiceGrantRecord
        {
            TenantId = fixture.Account.TenantId,
            CompanyId = fixture.Account.CompanyId,
            ServiceId = fixture.Auth.Service.Id,
            BindingId = binding.Id,
            Capability = GroupServiceCapability.Ingest,
            IsEnabled = true
        });
        await fixture.Auth.Db.SaveChangesAsync();
        var body = JsonSerializer.SerializeToUtf8Bytes(new GroupListenerPayload(external, new(fixture.Owner, GroupListenerOperation.Acquire, 0)), GroupServiceAuthenticator.JsonOptions);
        var signature = new GroupServiceSignature(fixture.Auth.Service.Id, 1, Fixture.Now.ToUnixTimeSeconds(), nonce, "");
        signature = signature with { SignatureHex = Convert.ToHexString(HMACSHA256.HashData(fixture.Auth.Secrets.Key, GroupServiceAuthenticator.ListenerSigningBytes(signature, body))) };
        var verified = await fixture.Auth.Authenticator.AuthenticateListenerAsync(signature, body);
        Assert.Equal(account.Id, verified.Account.ConnectorAccountId);
        await Assert.ThrowsAsync<GroupListenerConflictException>(() => fixture.Store.ApplyAsync(verified));
        Assert.Single(fixture.Auth.Db.GroupListenerLeases); Assert.Single(fixture.Auth.Db.GroupAccountCoverageGaps); Assert.Single(fixture.Auth.Db.GroupListenerCommandReceipts);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CurrentGrantChangedAtEitherSaveBoundaryCannotReleaseSuccessAck(int save)
    {
        using var fixture = new Fixture(); var verified = await fixture.VerifyAsync();
        using var altered = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>(fixture.Auth.Options).AddInterceptors(new AfterSave(save, () =>
        {
            fixture.Auth.Grant.IsEnabled = false; fixture.Auth.Db.SaveChanges();
        })).Options);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new GroupListenerStore(altered, Fixture.Policy, fixture.Auth.Clock).ApplyAsync(verified));
        Assert.DoesNotContain(altered.ChangeTracker.Entries(), x => x.Entity is GroupListenerLeaseRecord or GroupAccountCoverageGapRecord or GroupListenerCommandReceiptRecord);
        // A native external writer must queue behind the owned transaction;
        // this InMemory hook verifies final proof/no ACK, not SQL rollback.
    }

    [Theory]
    [InlineData(GroupListenerOperation.Acquire)]
    [InlineData(GroupListenerOperation.Stop)]
    public async Task LeaseWritePrecedesAccountCoverageAndReceiptWhileRemainingInSameOwnedUnit(GroupListenerOperation operation)
    {
        using var fixture = new Fixture();
        if (operation == GroupListenerOperation.Stop) await fixture.ApplyAsync();
        var verified = await fixture.VerifyAsync(new(fixture.Owner, operation, operation == GroupListenerOperation.Stop ? 1 : 0));
        var order = new RequireLeaseBeforeCoverage();
        using var ordered = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>(fixture.Auth.Options).AddInterceptors(order).Options);
        var receipt = await new GroupListenerStore(ordered, Fixture.Policy, fixture.Auth.Clock).ApplyAsync(verified);
        Assert.True(receipt.CoverageRecorded); Assert.Equal(2, order.Saves);
    }

    private sealed class Fixture : IDisposable
    {
        internal static readonly DateTimeOffset Now = GroupServiceAuthenticatorTests.Fixture.Now;
        internal static GroupIngressRuntimePolicy Policy => GroupIngressRuntimePolicy.OwnedSyntheticFixture("Development", true);
        internal readonly GroupServiceAuthenticatorTests.Fixture Auth = new();
        internal Guid Owner => Auth.Payload().ListenerOwnerId;
        internal GroupListenerAccountScope Account => new(Auth.Scope.TenantId, Auth.Scope.CompanyId, Auth.Account.Id);
        internal GroupListenerStore Store => new(Auth.Db, Policy, Auth.Clock);
        internal byte[] Body(GroupListenerCommand command) => JsonSerializer.SerializeToUtf8Bytes(new GroupListenerPayload(Auth.External, command), GroupServiceAuthenticator.JsonOptions);
        internal async Task<VerifiedGroupListener> VerifyAsync(GroupListenerCommand? command = null, Guid? nonce = null, bool originalSigningTime = false)
        {
            var body = Body(command ?? new(Owner, GroupListenerOperation.Acquire, 0));
            var signature = new GroupServiceSignature(Auth.Service.Id, Auth.Service.CredentialEpoch,
                (originalSigningTime ? Now : Auth.Clock.Current).ToUnixTimeSeconds(), nonce ?? Guid.NewGuid(), "");
            signature = signature with { SignatureHex = Convert.ToHexString(HMACSHA256.HashData(Auth.Secrets.Key, GroupServiceAuthenticator.ListenerSigningBytes(signature, body))) };
            return await Auth.Authenticator.AuthenticateListenerAsync(signature, body);
        }
        internal async Task<GroupListenerCommittedReceipt> ApplyAsync(GroupListenerCommand? command = null, Guid? nonce = null, bool originalSigningTime = false) =>
            await Store.ApplyAsync(await VerifyAsync(command, nonce, originalSigningTime));
        public void Dispose() => Auth.Dispose();
    }

    private sealed class AfterSave(int target, Action change) : SaveChangesInterceptor
    {
        private int calls;
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        { if (++calls == target) change(); return ValueTask.FromResult(result); }
    }
    private sealed class RequireLeaseBeforeCoverage : SaveChangesInterceptor
    {
        internal int Saves { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var changes = eventData.Context!.ChangeTracker.Entries().Where(x => x.State is EntityState.Added or EntityState.Modified).ToArray();
            if (++Saves == 1)
            {
                Assert.NotEmpty(changes);
                Assert.All(changes, x => Assert.IsType<GroupListenerLeaseRecord>(x.Entity));
            }
            else
            {
                Assert.Equal(2, Saves);
                Assert.Contains(changes, x => x.Entity is GroupAccountCoverageGapRecord && x.State == EntityState.Added);
                Assert.Contains(changes, x => x.Entity is GroupListenerCommandReceiptRecord && x.State == EntityState.Added);
                Assert.DoesNotContain(changes, x => x.Entity is GroupListenerLeaseRecord);
            }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class FailSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            throw new DbUpdateException("private underlying SQL-like fault");
    }
}
