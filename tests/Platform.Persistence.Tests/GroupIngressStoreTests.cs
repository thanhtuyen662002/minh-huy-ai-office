using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupIngressStoreTests
{
    [Fact]
    public async Task AcceptedSourceCreatesOneProtectedRevisionReceiptCommittedCursorAndReferenceOutbox()
    {
        using var fixture = new Fixture();
        var receipt = await fixture.Store.AcceptAsync(await fixture.VerifyAsync());
        Assert.False(receipt.WasAlreadyCommitted); Assert.Equal(1, receipt.CommittedSequence); Assert.Equal(1, receipt.Revision);
        var revision = Assert.Single(await fixture.Auth.Db.GroupMessageRevisions.ToListAsync());
        Assert.Equal(fixture.Auth.Payload().Text, new GroupSourceContentProtector().Unprotect(new(fixture.Auth.Scope, receipt.MessageId, 1, 1, 0), revision.ProtectedContent, fixture.Keys.Key, revision.ContentKeyId));
        Assert.False(revision.ProtectedContent.AsSpan().IndexOf(Encoding.UTF8.GetBytes(fixture.Auth.Payload().Text)) >= 0);
        var outbox = Assert.Single(await fixture.Auth.Db.GroupIngressOutbox.ToListAsync());
        Assert.Equal((receipt.MessageId, receipt.Revision, receipt.CommittedSequence), (outbox.MessageId, outbox.Revision, outbox.CommittedSequence));
        Assert.Null(outbox.PublishedAtUtc); Assert.Equal(0, outbox.PublishAttempts);
        var cursor = Assert.Single(await fixture.Auth.Db.GroupSourceStates.ToListAsync());
        Assert.Equal(1, cursor.CommittedSequence); Assert.Equal(Fixture.Now, cursor.FirstPendingAtUtc); Assert.Equal(Fixture.Now, cursor.LastPendingAtUtc);
        Assert.Single(await fixture.Auth.Db.GroupIngressReceipts.ToListAsync());
        Assert.Empty(await fixture.Auth.Db.Tasks.ToListAsync());
    }

    [Fact]
    public async Task HundredIdenticalReplaysAndNewListenerEpochRetainOneOriginalReceiptAndEffect()
    {
        using var fixture = new Fixture(); var verified = await fixture.VerifyAsync();
        var first = await fixture.Store.AcceptAsync(verified);
        for (var i = 0; i < 100; i++)
        {
            var replay = await fixture.Store.AcceptAsync(verified);
            Assert.True(replay.WasAlreadyCommitted); Assert.Equal(first with { WasAlreadyCommitted = true }, replay);
        }
        fixture.Lease.OwnerId = Guid.NewGuid(); fixture.Lease.Epoch++; await fixture.Auth.Db.SaveChangesAsync();
        var recovered = await fixture.VerifyAsync(fixture.Auth.Payload() with { ListenerOwnerId = fixture.Lease.OwnerId, ListenerEpoch = fixture.Lease.Epoch });
        Assert.Equal(first with { WasAlreadyCommitted = true }, await fixture.Store.AcceptAsync(recovered));
        Assert.Equal(1, fixture.Keys.Calls);
        Assert.Single(await fixture.Auth.Db.GroupIngressOutbox.ToListAsync()); Assert.Single(await fixture.Auth.Db.GroupMessages.ToListAsync());
        Assert.Single(await fixture.Auth.Db.GroupMessageRevisions.ToListAsync());
    }

    [Theory]
    [InlineData("text")]
    [InlineData("sender")]
    [InlineData("reply")]
    [InlineData("message")]
    [InlineData("history")]
    [InlineData("occurred")]
    public async Task SameEventIdentityWithChangedLogicalEnvelopeRefusesWithoutReplacingOriginal(string change)
    {
        using var fixture = new Fixture(); await fixture.Store.AcceptAsync(await fixture.VerifyAsync());
        var payload = fixture.Auth.Payload();
        payload = change switch
        {
            "text" => WithText(payload, "other"),
            "sender" => payload with { Event = payload.Event with { SenderId = "other-sender" } },
            "reply" => payload with { Event = payload.Event with { ReplyToMessageId = "other-message" } },
            "message" => payload with { Event = payload.Event with { MessageId = "other-message" } },
            "history" => payload with { Event = payload.Event with { IsHistoricalBackfill = true } },
            _ => payload with { Event = payload.Event with { OccurredAtUtc = Fixture.Now.AddSeconds(-1) } }
        };
        var changed = await fixture.VerifyAsync(payload);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.AcceptAsync(changed));
        Assert.Equal("Group ingress event conflicts with its committed identity.", error.Message);
        Assert.Single(await fixture.Auth.Db.GroupIngressReceipts.ToListAsync()); Assert.Single(await fixture.Auth.Db.GroupMessageRevisions.ToListAsync());
        Assert.Equal(1, fixture.Keys.Calls);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("epoch")]
    [InlineData("expiry")]
    [InlineData("heartbeat")]
    [InlineData("account")]
    public async Task MissingStaleAndForeignListenerRefuseBeforeSourceKeyAndStorage(string change)
    {
        using var fixture = new Fixture(); var verified = await fixture.VerifyAsync();
        switch (change)
        {
            case "owner": fixture.Lease.OwnerId = Guid.NewGuid(); break;
            case "epoch": fixture.Lease.Epoch++; break;
            case "expiry": fixture.Lease.ExpiresAtUtc = Fixture.Now; break;
            case "heartbeat": fixture.Lease.HeartbeatAtUtc = Fixture.Now.AddSeconds(1); break;
            case "account": fixture.Auth.Db.Remove(fixture.Lease); break;
        }
        await fixture.Auth.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Store.AcceptAsync(verified));
        Assert.Equal(0, fixture.Keys.Calls); await fixture.AssertEmptyAsync();
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("source-version")]
    [InlineData("key-failure")]
    [InlineData("expired-during-key")]
    public async Task ChangedAuthorityOrUnavailableKeyCannotCommitStagedSourceOrOutbox(string change)
    {
        using var fixture = new Fixture(); var verified = await fixture.VerifyAsync();
        fixture.Keys.BeforeResolution = async () =>
        {
            switch (change)
            {
                case "grant": fixture.Auth.Grant.IsEnabled = false; break;
                case "source-version": fixture.Auth.Binding.Version++; break;
                case "key-failure": throw new InvalidOperationException("owned key unavailable");
                case "expired-during-key": fixture.Clock.Now = fixture.Lease.ExpiresAtUtc; return;
            }
            // Simulate an independently committed registry change; detach staged
            // source before fixture SaveChanges so it cannot manufacture effects.
            foreach (var entry in fixture.Auth.Db.ChangeTracker.Entries().Where(x => x.State == EntityState.Added).ToArray()) entry.State = EntityState.Detached;
            await fixture.Auth.Db.SaveChangesAsync();
        };
        if (change == "key-failure")
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.AcceptAsync(verified));
        else await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Store.AcceptAsync(verified));
        await fixture.AssertEmptyAsync();
    }

    [Fact]
    public async Task AppendOnlyEditRecallAndLateOriginalPreserveGapAndHistoryMetadata()
    {
        using var fixture = new Fixture();
        fixture.AllowRevisions();
        var edit = WithText(fixture.Auth.Payload() with { Event = fixture.Auth.Payload().Event with { Kind = GroupSourceEventKind.Edit, RevisionEventId = "edit" } }, "edited");
        var first = await fixture.Store.AcceptAsync(await fixture.VerifyAsync(edit));
        var gap = Assert.Single(await fixture.Auth.Db.GroupCoverageGaps.ToListAsync()); Assert.Equal("original-message-unseen", gap.Reason);
        var original = fixture.Auth.Payload() with { Event = fixture.Auth.Payload().Event with { IsHistoricalBackfill = true } };
        var second = await fixture.Store.AcceptAsync(await fixture.VerifyAsync(original));
        var recall = WithText(edit with { Event = edit.Event with { Kind = GroupSourceEventKind.Recall, RevisionEventId = "recall" } }, "");
        var third = await fixture.Store.AcceptAsync(await fixture.VerifyAsync(recall));
        Assert.Equal(first.MessageId, second.MessageId); Assert.Equal(first.MessageId, third.MessageId);
        Assert.Equal(new long[] { 1, 2, 3 }, await fixture.Auth.Db.GroupMessageRevisions.OrderBy(x => x.CommittedSequence).Select(x => x.CommittedSequence).ToArrayAsync());
        Assert.Equal(3, third.Revision); Assert.True((await fixture.Auth.Db.GroupMessageRevisions.SingleAsync(x => x.Revision == 2)).IsHistoricalBackfill);
        Assert.Equal(3, await fixture.Auth.Db.GroupIngressOutbox.CountAsync()); Assert.Null(gap.ReconnectedAtUtc);
        Assert.Empty(await fixture.Auth.Db.Tasks.ToListAsync());
    }

    [Fact]
    public async Task NewOriginalCannotOverwriteExistingMessageAndNonEmptyRecallRefuses()
    {
        using var fixture = new Fixture(); await fixture.Store.AcceptAsync(await fixture.VerifyAsync()); fixture.AllowRevisions();
        var differentEvent = fixture.Auth.Payload() with { Event = fixture.Auth.Payload().Event with { RevisionEventId = "different-original" } };
        var changed = await fixture.VerifyAsync(differentEvent);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.AcceptAsync(changed));
        var invalidRecall = differentEvent with { Event = differentEvent.Event with { Kind = GroupSourceEventKind.Recall } };
        var recalled = await fixture.VerifyAsync(invalidRecall);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.AcceptAsync(recalled));
        Assert.Single(await fixture.Auth.Db.GroupMessageRevisions.ToListAsync()); Assert.Single(await fixture.Auth.Db.GroupIngressOutbox.ToListAsync());
    }

    [Fact]
    public async Task FreshCursorAvoidsTrackedSnapshotAndSequenceOverflowRefusesBeforeKeyResolution()
    {
        using var fixture = new Fixture(); await fixture.Store.AcceptAsync(await fixture.VerifyAsync());
        var state = await fixture.Auth.Db.GroupSourceStates.SingleAsync(); state.CommittedSequence = long.MaxValue; await fixture.Auth.Db.SaveChangesAsync();
        var next = fixture.Auth.Payload() with { Event = fixture.Auth.Payload().Event with { MessageId = "next", RevisionEventId = "next" } };
        var nextVerified = await fixture.VerifyAsync(next);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.AcceptAsync(nextVerified));
        Assert.Equal(1, fixture.Keys.Calls); Assert.Single(await fixture.Auth.Db.GroupIngressOutbox.ToListAsync());
        Assert.Equal(long.MaxValue, (await fixture.Auth.Db.GroupSourceStates.SingleAsync()).CommittedSequence);
    }

    [Fact]
    public async Task AnotherCommittedContextCannotBeSkippedByAnOldTrackedSourceCursor()
    {
        using var fixture = new Fixture(); await fixture.Store.AcceptAsync(await fixture.VerifyAsync());
        var tracked = await fixture.Auth.Db.GroupSourceStates.SingleAsync(); Assert.Equal(1, tracked.CommittedSequence);
        using (var other = new PlatformDbContext(fixture.Auth.Options))
        {
            var next = fixture.Auth.Payload() with { Event = fixture.Auth.Payload().Event with { MessageId = "second", RevisionEventId = "second" } };
            var otherStore = new GroupIngressStore(other, fixture.Keys, new(), GroupIngressRuntimePolicy.OwnedSyntheticFixture("Development", true), fixture.Clock);
            Assert.Equal(2, (await otherStore.AcceptAsync(await fixture.VerifyAsync(next))).CommittedSequence);
        }
        Assert.Equal(1, tracked.CommittedSequence);
        var third = fixture.Auth.Payload() with { Event = fixture.Auth.Payload().Event with { MessageId = "third", RevisionEventId = "third" } };
        Assert.Equal(3, (await fixture.Store.AcceptAsync(await fixture.VerifyAsync(third))).CommittedSequence);
        Assert.Equal(new long[] { 1, 2, 3 }, await fixture.Auth.Db.GroupIngressOutbox.OrderBy(x => x.CommittedSequence).Select(x => x.CommittedSequence).ToArrayAsync());
    }

    private static GroupIngressPayload WithText(GroupIngressPayload payload, string text) => payload with
    {
        Text = text,
        Event = payload.Event with { ContentSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))) }
    };
    private sealed class Fixture : IDisposable
    {
        internal static DateTimeOffset Now => GroupServiceAuthenticatorTests.Fixture.Now;
        internal readonly GroupServiceAuthenticatorTests.Fixture Auth = new();
        internal readonly OwnedKeys Keys = new(); internal readonly OwnedClock Clock = new();
        internal readonly GroupListenerLeaseRecord Lease;
        internal GroupIngressStore Store => new(Auth.Db, Keys, new(), GroupIngressRuntimePolicy.OwnedSyntheticFixture("Development", true), Clock);
        internal Fixture()
        {
            Lease = new()
            {
                TenantId = Auth.Scope.TenantId,
                CompanyId = Auth.Scope.CompanyId,
                ConnectorAccountId = Auth.Account.Id,
                OwnerId = Auth.Payload().ListenerOwnerId,
                Epoch = 1,
                HeartbeatAtUtc = Now,
                ExpiresAtUtc = Now.AddMinutes(2)
            };
            Auth.Db.Add(Lease); Auth.Db.SaveChanges();
        }
        internal async Task<VerifiedGroupIngress> VerifyAsync(GroupIngressPayload? payload = null)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(payload ?? Auth.Payload(), GroupServiceAuthenticator.JsonOptions);
            return await Auth.Authenticator.AuthenticateAsync(Auth.Sign(body: body), body);
        }
        internal void AllowRevisions()
        {
            Auth.Account.QualificationJson = JsonSerializer.Serialize(new
            {
                Environment = GroupQualificationEnvironment.Synthetic,
                Observations = new[] { new GroupConnectorObservation(GroupConnectorCapability.EditEvents, GroupConnectorSupport.Supported, Guid.NewGuid(), Now),
                    new GroupConnectorObservation(GroupConnectorCapability.RecallEvents, GroupConnectorSupport.Supported, Guid.NewGuid(), Now) }
            }, GroupServiceAuthenticator.JsonOptions);
            Auth.Db.SaveChanges();
        }
        internal async Task AssertEmptyAsync()
        {
            Assert.Empty(await Auth.Db.GroupMessages.ToListAsync()); Assert.Empty(await Auth.Db.GroupMessageRevisions.ToListAsync());
            Assert.Empty(await Auth.Db.GroupIngressReceipts.ToListAsync()); Assert.Empty(await Auth.Db.GroupIngressOutbox.ToListAsync()); Assert.Empty(await Auth.Db.GroupSourceStates.ToListAsync());
        }
        public void Dispose() => Auth.Dispose();
    }
    private sealed class OwnedClock : TimeProvider
    {
        internal DateTimeOffset Now = Fixture.Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class OwnedKeys : IGroupSourceKeyProvider
    {
        internal readonly byte[] Key = Enumerable.Repeat((byte)0x32, 32).ToArray();
        internal int Calls; internal Func<Task>? BeforeResolution;
        public async ValueTask<GroupSourceKeyMaterial> ResolveWriteAsync(GroupScope source, CancellationToken cancellationToken = default)
        { Calls++; if (BeforeResolution is not null) await BeforeResolution(); return new("owned", Key); }
        public ValueTask<GroupSourceKeyMaterial> ResolveReadAsync(GroupScope source, string keyId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
