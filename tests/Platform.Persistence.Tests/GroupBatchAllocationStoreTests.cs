using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupBatchAllocationStoreTests
{
    [Fact]
    public async Task QuietPeriodIsDurableAndEligibilityDoesNotDependOnInboxOrPortalClicks()
    {
        using var fixture = new Fixture(1);
        fixture.Auth.Clock.Current = fixture.First.AddSeconds(29);
        Assert.Null(await fixture.Store.AllocateDueAsync(fixture.Scope, Guid.NewGuid()));
        Assert.Equal(0, (await fixture.StateAsync()).ScheduledThroughSequence);
        fixture.Auth.Clock.Current = fixture.First.AddSeconds(30);
        var receipt = Assert.IsType<GroupBatchAllocationReceipt>(await fixture.Store.AllocateDueAsync(fixture.Scope, Guid.NewGuid()));
        Assert.Equal(1, receipt.AllocatedThroughSequence); Assert.False(receipt.WasAlreadyAllocated);
        var state = await fixture.StateAsync(); Assert.Null(state.FirstPendingAtUtc); Assert.Null(state.LastPendingAtUtc);
        Assert.Empty(await fixture.Auth.Db.GroupIngressInbox.ToListAsync());
        Assert.Empty(await fixture.Auth.Db.Tasks.ToListAsync()); Assert.Empty(await fixture.Auth.Db.Users.ToListAsync());
        Assert.Equal(0, fixture.Auth.Secrets.Calls); Assert.False(fixture.Auth.Db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task ContinuousTrafficReachesMaximumWaitWithoutResettingToAllocationTime()
    {
        using var fixture = new Fixture(1200, 1200);
        var state = await fixture.StateAsync();
        state.LastPendingAtUtc = fixture.First.AddSeconds(119);
        fixture.Auth.Db.ChangeTracker.Clear(); fixture.Auth.Db.Update(state); await fixture.Auth.Db.SaveChangesAsync();
        fixture.Auth.Clock.Current = fixture.First.AddSeconds(119);
        Assert.Null(await fixture.Store.AllocateDueAsync(fixture.Scope, Guid.NewGuid()));
        fixture.Auth.Clock.Current = fixture.First.AddSeconds(120);
        var receipts = new List<GroupBatchAllocationReceipt>();
        // Raw revision cap is independent of message count and provider bytes.
        while ((await fixture.StateAsync()).ScheduledThroughSequence < 1200)
        {
            var receipt = Assert.IsType<GroupBatchAllocationReceipt>(await fixture.Store.AllocateDueAsync(fixture.Scope, Guid.NewGuid()));
            receipts.Add(receipt);
            var suffix = await fixture.StateAsync();
            if (suffix.ScheduledThroughSequence < 1200)
            {
                Assert.Equal(fixture.First, suffix.FirstPendingAtUtc);
                Assert.Equal(fixture.First.AddSeconds(119), suffix.LastPendingAtUtc);
            }
        }
        Assert.Equal(new[] { 500, 500, 200 }, receipts.Select(x => x.Revisions.Count));
        Assert.Equal(Enumerable.Range(1, 1200).Select(x => (long)x), receipts.SelectMany(x => x.Revisions).Select(x => x.Metadata.CommittedSequence));
        Assert.Equal(3, await fixture.Auth.Db.GroupBatchAllocations.CountAsync());
        Assert.Equal(1200, await fixture.Auth.Db.GroupBatchAllocatedRevisions.CountAsync());
        Assert.Null((await fixture.StateAsync()).FirstPendingAtUtc);
    }

    [Fact]
    public async Task SuffixReanchorsToOriginalCommitAndRetainsFinalProofLatencyAcrossLaterTraffic()
    {
        using var fixture = new Fixture(101);
        var tail = await fixture.Auth.Db.GroupMessageRevisions.SingleAsync(x => x.CommittedSequence == 101);
        var tailReceipt = await fixture.Auth.Db.GroupIngressReceipts.SingleAsync(x => x.MessageId == tail.MessageId && x.Revision == tail.Revision);
        tail.CommittedAtUtc = fixture.First.AddSeconds(5); tailReceipt.CommittedAtUtc = tail.CommittedAtUtc;
        var state = await fixture.Auth.Db.GroupSourceStates.SingleAsync(); state.LastPendingAtUtc = fixture.First.AddSeconds(9);
        await fixture.Auth.Db.SaveChangesAsync();
        var first = Assert.IsType<GroupBatchAllocationReceipt>(await fixture.Store.AllocateDueAsync(fixture.Scope, Guid.NewGuid()));
        Assert.Equal(100, first.AllocatedThroughSequence);
        state = await fixture.StateAsync();
        Assert.Equal(fixture.First.AddSeconds(5), state.FirstPendingAtUtc);
        Assert.Equal(fixture.First.AddSeconds(9), state.LastPendingAtUtc);
        fixture.Append(102, 1); await fixture.Auth.Db.SaveChangesAsync();
        state.CommittedSequence = 102; state.LastPendingAtUtc = fixture.First.AddSeconds(125);
        fixture.Auth.Db.Update(state); await fixture.Auth.Db.SaveChangesAsync();
        fixture.Auth.Clock.Current = fixture.First.AddSeconds(125);
        var suffix = Assert.IsType<GroupBatchAllocationReceipt>(await fixture.Store.AllocateDueAsync(fixture.Scope, Guid.NewGuid()));
        Assert.Equal(new long[] { 101, 102 }, suffix.Revisions.Select(x => x.Metadata.CommittedSequence));
        Assert.Null((await fixture.StateAsync()).FirstPendingAtUtc);
    }

    [Fact]
    public async Task AuthorizedEmptySourceWithoutCursorIsAnHonestNoAllocation()
    {
        using var auth = new GroupServiceAuthenticatorTests.Fixture("capability");
        var store = new GroupBatchAllocationStore(auth.Db, new(auth.Scope.TenantId, auth.Scope.CompanyId, auth.Service.Id, 1),
            GroupBatchTiming.InitialTuning, auth.Clock);
        Assert.Null(await store.AllocateDueAsync(auth.Scope, Guid.NewGuid()));
        Assert.Empty(await auth.Db.GroupBatchAllocations.ToListAsync()); Assert.Equal(0, auth.Secrets.Calls);
    }

    [Fact]
    public async Task HistoricalBoundaryAndHundredDistinctIdsRetainCompleteOrderedSuffix()
    {
        using var fixture = new Fixture(110, 1, 7);
        var first = Assert.IsType<GroupBatchAllocationReceipt>(await fixture.Store.AllocateDueAsync(fixture.Scope, Guid.NewGuid()));
        Assert.True(first.IsHistoricalBackfill); Assert.Equal(7, first.Revisions.Count);
        var live = Assert.IsType<GroupBatchAllocationReceipt>(await fixture.Store.AllocateDueAsync(fixture.Scope, Guid.NewGuid()));
        Assert.False(live.IsHistoricalBackfill); Assert.Equal(100, live.Revisions.Count);
        var tail = Assert.IsType<GroupBatchAllocationReceipt>(await fixture.Store.AllocateDueAsync(fixture.Scope, Guid.NewGuid()));
        Assert.Equal(3, tail.Revisions.Count);
        Assert.Equal(Enumerable.Range(1, 110).Select(x => (long)x), new[] { first, live, tail }.SelectMany(x => x.Revisions).Select(x => x.Metadata.CommittedSequence));
    }

    [Fact]
    public async Task OriginalNonceReplaysAfterProcessReplacementAndUnrelatedNewIngress()
    {
        using var fixture = new Fixture(1);
        var operation = Guid.NewGuid();
        var original = Assert.IsType<GroupBatchAllocationReceipt>(await fixture.Store.AllocateDueAsync(fixture.Scope, operation));
        // Fresh DbContext emulates process replacement, not a cached receipt.
        await using var restarted = new PlatformDbContext(fixture.Auth.Options);
        var store = fixture.StoreFor(restarted);
        for (var i = 0; i < 100; i++)
        {
            var replay = Assert.IsType<GroupBatchAllocationReceipt>(await store.AllocateDueAsync(fixture.Scope, operation));
            Assert.True(replay.WasAlreadyAllocated); Assert.Equal(original.BatchId, replay.BatchId);
            Assert.Equal(original.AllocatedAtUtc, replay.AllocatedAtUtc); Assert.Equal(original.Revisions, replay.Revisions);
        }
        fixture.Append(2, 1); await fixture.Auth.Db.SaveChangesAsync();
        var state = await fixture.StateAsync(); state.CommittedSequence = 2;
        state.FirstPendingAtUtc = fixture.First; state.LastPendingAtUtc = fixture.First;
        fixture.Auth.Db.ChangeTracker.Clear(); fixture.Auth.Db.Update(state); await fixture.Auth.Db.SaveChangesAsync();
        var afterNewIngress = Assert.IsType<GroupBatchAllocationReceipt>(await store.AllocateDueAsync(fixture.Scope, operation));
        Assert.Equal(1, afterNewIngress.ObservedCommittedThroughSequence); Assert.Equal(1, afterNewIngress.AllocatedThroughSequence);
        Assert.Equal(1, (await fixture.StateAsync()).ScheduledThroughSequence);
        Assert.Equal(1, await fixture.Auth.Db.GroupBatchAllocations.CountAsync()); Assert.Equal(0, fixture.Auth.Secrets.Calls);
        Assert.Throws<NotSupportedException>(() => ((IList<GroupAllocatedRevision>)afterNewIngress.Revisions).Clear());
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("service")]
    [InlineData("epoch")]
    [InlineData("source")]
    [InlineData("role")]
    [InlineData("account")]
    [InlineData("alias")]
    [InlineData("company")]
    public async Task CurrentExtractAuthorityIsRequiredBeforeAllocationAndOriginalReplay(string change)
    {
        using var fixture = new Fixture(1);
        var operation = Guid.NewGuid();
        var original = Assert.IsType<GroupBatchAllocationReceipt>(await fixture.Store.AllocateDueAsync(fixture.Scope, operation));
        switch (change)
        {
            case "grant": fixture.Auth.Grant.IsEnabled = false; break;
            case "service": fixture.Auth.Service.IsEnabled = false; break;
            case "epoch": fixture.Auth.Service.CredentialEpoch++; break;
            case "source": fixture.Auth.Binding.IsEnabled = false; break;
            case "role": fixture.Auth.Binding.Role = GroupBindingRole.TechnicalInternal; break;
            case "account": fixture.Auth.Account.IsEnabled = false; break;
            case "alias": fixture.Auth.Account.ExternalAccountId = fixture.Auth.External.AccountId.TrimEnd(); break;
            case "company": (await fixture.Auth.Db.Companies.SingleAsync()).IsActive = false; break;
        }
        await fixture.Auth.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Store.AllocateDueAsync(fixture.Scope, operation));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Store.AllocateDueAsync(fixture.Scope, Guid.NewGuid()));
        Assert.Equal(original.BatchId, (await fixture.Auth.Db.GroupBatchAllocations.SingleAsync()).Id);
        Assert.Equal(0, fixture.Auth.Secrets.Calls);
    }

    [Fact]
    public async Task AuthorityChangedAfterInitialReadIsFencedBeforeReservationCommit()
    {
        using var fixture = new Fixture(1);
        var clock = new MutatingClock(fixture.Auth.Clock.Current, () =>
        {
            fixture.Auth.Binding.Version++;
            fixture.Auth.Db.SaveChanges();
        });
        var store = new GroupBatchAllocationStore(fixture.Auth.Db,
            new(fixture.Scope.TenantId, fixture.Scope.CompanyId, fixture.Auth.Service.Id, 1), GroupBatchTiming.InitialTuning, clock);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.AllocateDueAsync(fixture.Scope, Guid.NewGuid()));
        Assert.Equal(0, (await fixture.StateAsync()).ScheduledThroughSequence);
        Assert.Empty(await fixture.Auth.Db.GroupBatchAllocations.ToListAsync());
        Assert.Empty(await fixture.Auth.Db.GroupBatchAllocatedRevisions.ToListAsync());
        Assert.False(fixture.Auth.Db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task OldGenerationRevisionsAreOnlyMetadataReservationsUnderNewCurrentAuthority()
    {
        using var fixture = new Fixture(1);
        fixture.Auth.Binding.Version++; fixture.Auth.Binding.DeletionGeneration++;
        fixture.Auth.Account.Version++; fixture.Auth.Grant.Version++;
        await fixture.Auth.Db.SaveChangesAsync();
        var receipt = Assert.IsType<GroupBatchAllocationReceipt>(await fixture.Store.AllocateDueAsync(fixture.Scope, Guid.NewGuid()));
        Assert.Equal(2, receipt.SourceVersion); Assert.Equal(1, receipt.DeletionGeneration);
        Assert.Equal(2, receipt.AccountVersion); Assert.Equal(2, receipt.GrantVersion);
        Assert.Equal(1, receipt.Revisions[0].SourceVersion); Assert.Equal(0, receipt.Revisions[0].DeletionGeneration);
        Assert.Equal(0, fixture.Auth.Secrets.Calls);
    }

    [Theory]
    [InlineData("missing-receipt")]
    [InlineData("duplicate-receipt")]
    [InlineData("wrong-receipt-time")]
    [InlineData("future")]
    [InlineData("non-utc")]
    [InlineData("sha")]
    [InlineData("kind")]
    [InlineData("sequence-gap")]
    [InlineData("version")]
    [InlineData("pending-null")]
    [InlineData("pending-future")]
    public async Task MalformedPendingPrefixOrSentinelCannotAdvanceAnyCursor(string change)
    {
        using var fixture = new Fixture(501, 501);
        var last = await fixture.Auth.Db.GroupMessageRevisions.OrderBy(x => x.CommittedSequence).LastAsync();
        var receipt = await fixture.Auth.Db.GroupIngressReceipts.SingleAsync(x => x.MessageId == last.MessageId && x.Revision == last.Revision);
        switch (change)
        {
            case "missing-receipt": fixture.Auth.Db.Remove(receipt); break;
            case "duplicate-receipt":
                fixture.Auth.Db.Add(new GroupIngressReceiptRecord
                {
                    TenantId = receipt.TenantId,
                    CompanyId = receipt.CompanyId,
                    BindingId = receipt.BindingId,
                    EventIdentityHash = new string('C', 64),
                    MessageId = receipt.MessageId,
                    Revision = receipt.Revision,
                    ServiceId = receipt.ServiceId,
                    CredentialEpoch = 1,
                    ListenerEpoch = 1,
                    CommittedAtUtc = receipt.CommittedAtUtc
                }); break;
            case "wrong-receipt-time": receipt.CommittedAtUtc += TimeSpan.FromTicks(1); break;
            case "future": last.CommittedAtUtc = fixture.Auth.Clock.Current.AddSeconds(1); receipt.CommittedAtUtc = last.CommittedAtUtc; break;
            case "non-utc": last.CommittedAtUtc = last.CommittedAtUtc.ToOffset(TimeSpan.FromHours(1)); break;
            case "sha": last.ContentSha256 = new string('b', 64); break;
            case "kind": last.Kind = (GroupSourceEventKind)99; break;
            case "sequence-gap": last.CommittedSequence++; break;
            case "version": last.SourceVersion = 0; break;
            case "pending-null":
            case "pending-future":
                var state = await fixture.StateAsync();
                state.FirstPendingAtUtc = change == "pending-null" ? null : fixture.Auth.Clock.Current.AddSeconds(1);
                state.LastPendingAtUtc = state.FirstPendingAtUtc;
                fixture.Auth.Db.ChangeTracker.Clear(); fixture.Auth.Db.Update(state); break;
        }
        await fixture.Auth.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.AllocateDueAsync(fixture.Scope, Guid.NewGuid()));
        Assert.Equal(0, (await fixture.StateAsync()).ScheduledThroughSequence);
        Assert.Empty(await fixture.Auth.Db.GroupBatchAllocations.ToListAsync());
        Assert.Empty(await fixture.Auth.Db.GroupBatchAllocatedRevisions.ToListAsync());
        Assert.False(fixture.Auth.Db.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData("ledger-digest")]
    [InlineData("ledger-version")]
    [InlineData("missing-ledger")]
    [InlineData("batch-count")]
    [InlineData("batch-history")]
    [InlineData("missing-original-receipt")]
    public async Task CorruptOriginalReservationIsNeverReplacedByASecondSuccess(string change)
    {
        using var fixture = new Fixture(1); var operation = Guid.NewGuid();
        await fixture.Store.AllocateDueAsync(fixture.Scope, operation);
        var row = await fixture.Auth.Db.GroupBatchAllocatedRevisions.SingleAsync();
        var batch = await fixture.Auth.Db.GroupBatchAllocations.SingleAsync();
        switch (change)
        {
            case "ledger-digest": row.ContentSha256 = new string('C', 64); break;
            case "ledger-version": row.SourceVersion++; break;
            case "missing-ledger": fixture.Auth.Db.Remove(row); break;
            case "batch-count": batch.RawRevisionCount++; break;
            case "batch-history": batch.IsHistoricalBackfill = true; break;
            case "missing-original-receipt": fixture.Auth.Db.Remove(await fixture.Auth.Db.GroupIngressReceipts.SingleAsync()); break;
        }
        await fixture.Auth.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.AllocateDueAsync(fixture.Scope, operation));
        Assert.Equal(1, await fixture.Auth.Db.GroupBatchAllocations.CountAsync());
        Assert.Equal(1, (await fixture.StateAsync()).ScheduledThroughSequence);
    }

    [Fact]
    public async Task ForeignScopeAndCancellationFailBeforeOpeningDisposedDatabase()
    {
        using var fixture = new Fixture(1); fixture.Auth.Db.Dispose();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Store.AllocateDueAsync(fixture.Scope with { CompanyId = Guid.NewGuid() }, Guid.NewGuid()));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Store.AllocateDueAsync(fixture.Scope, Guid.NewGuid(), canceled.Token));
    }

    [Fact]
    public void NativeModelUsesScopedRestrictiveKeysUniqueReservationsAndNoPrivatePayloadOrTerminalClaim()
    {
        using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer("Server=localhost;Database=Owned_Model;User Id=model;Password=model").Options);
        var model = db.GetService<IDesignTimeModel>().Model;
        var allocation = model.FindEntityType(typeof(GroupBatchAllocationRecord))!;
        var ledger = model.FindEntityType(typeof(GroupBatchAllocatedRevisionRecord))!;
        foreach (var entity in new[] { allocation, ledger })
        {
            Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId" }, entity.FindPrimaryKey()!.Properties.Take(3).Select(x => x.Name));
            foreach (var fk in entity.GetForeignKeys()) Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior);
            foreach (var forbidden in new[] { "ProtectedContent", "Text", "NotesJson", "UserId", "TaskId", "TerminalThroughSequence" }) Assert.Null(entity.FindProperty(forbidden));
        }
        Assert.Contains(allocation.GetIndexes(), x => x.IsUnique && x.Properties.Last().Name == "OperationId");
        Assert.Contains(allocation.GetIndexes(), x => x.IsUnique && x.Properties.Last().Name == "AfterSequence");
        Assert.Contains(ledger.GetIndexes(), x => x.IsUnique && x.Properties.Last().Name == "CommittedSequence");
        Assert.Contains(ledger.GetForeignKeys(), x => x.PrincipalEntityType.ClrType == typeof(GroupMessageRevisionRecord));
    }

    private sealed class MutatingClock(DateTimeOffset now, Action change) : TimeProvider
    {
        private bool changed;
        public override DateTimeOffset GetUtcNow()
        {
            if (!changed) { changed = true; change(); }
            return now;
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly GroupServiceAuthenticatorTests.Fixture Auth = new("capability");
        internal GroupScope Scope => Auth.Scope;
        internal readonly DateTimeOffset First = GroupServiceAuthenticatorTests.Fixture.Now.AddSeconds(-120);
        private readonly Dictionary<int, Guid> messages = [];
        internal GroupBatchAllocationStore Store => StoreFor(Auth.Db);
        internal GroupBatchAllocationStore StoreFor(PlatformDbContext db) => new(db, new(Scope.TenantId, Scope.CompanyId, Auth.Service.Id, 1), GroupBatchTiming.InitialTuning, Auth.Clock);
        internal Fixture(int count, int revisionsPerMessage = 1, int historyCount = 0)
        {
            for (var sequence = 1; sequence <= count; sequence++) Append(sequence, revisionsPerMessage, sequence <= historyCount);
            Auth.Db.Add(new GroupSourceStateRecord
            {
                TenantId = Scope.TenantId,
                CompanyId = Scope.CompanyId,
                BindingId = Scope.SourceBindingId,
                CommittedSequence = count,
                FirstPendingAtUtc = First,
                LastPendingAtUtc = First
            });
            Auth.Db.SaveChanges();
        }
        internal void Append(int sequence, int revisionsPerMessage, bool history = false)
        {
            var number = (sequence - 1) / revisionsPerMessage;
            var revision = (sequence - 1) % revisionsPerMessage + 1;
            if (!messages.TryGetValue(number, out var id))
            {
                id = Guid.NewGuid(); messages.Add(number, id);
                Auth.Db.Add(new GroupMessageRecord
                {
                    TenantId = Scope.TenantId,
                    CompanyId = Scope.CompanyId,
                    BindingId = Scope.SourceBindingId,
                    Id = id,
                    ExternalMessageId = "owned-message-" + number,
                    IdentityHash = sequence.ToString("X64")
                });
            }
            Auth.Db.Add(new GroupMessageRevisionRecord
            {
                TenantId = Scope.TenantId,
                CompanyId = Scope.CompanyId,
                BindingId = Scope.SourceBindingId,
                MessageId = id,
                Revision = revision,
                CommittedSequence = sequence,
                SourceVersion = 1,
                DeletionGeneration = 0,
                Kind = revision == 1 ? GroupSourceEventKind.NewText : GroupSourceEventKind.Edit,
                ContentSha256 = new string('B', 64),
                ContentKeyId = "owned-inert-key",
                ProtectedContent = new byte[29],
                ExternalRevisionEventId = "owned-event-" + sequence,
                SenderId = "owned-sender",
                OccurredAtUtc = First,
                CommittedAtUtc = First,
                IsHistoricalBackfill = history
            });
            Auth.Db.Add(new GroupIngressReceiptRecord
            {
                TenantId = Scope.TenantId,
                CompanyId = Scope.CompanyId,
                BindingId = Scope.SourceBindingId,
                EventIdentityHash = sequence.ToString("X64"),
                EnvelopeSha256 = new string('B', 64),
                ExternalRevisionEventId = "owned-event-" + sequence,
                MessageId = id,
                Revision = revision,
                ServiceId = Auth.Service.Id,
                CredentialEpoch = 1,
                ListenerEpoch = 1,
                CommittedAtUtc = First
            });
        }
        internal Task<GroupSourceStateRecord> StateAsync() => Auth.Db.GroupSourceStates.AsNoTracking().SingleAsync();
        public void Dispose() => Auth.Dispose();
    }
}
