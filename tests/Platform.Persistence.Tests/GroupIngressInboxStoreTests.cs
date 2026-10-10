using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupIngressInboxStoreTests
{
    [Fact]
    public async Task HundredReferenceRedeliveriesRetainOneOriginalInboxWithoutSourceCursorOrPortalEffects()
    {
        using var fixture = new Fixture();
        var original = await fixture.Store.ReceiveAsync(fixture.Reference);
        Assert.False(original.WasAlreadyReceived);
        fixture.Auth.Clock.Current += TimeSpan.FromSeconds(1);
        for (var i = 0; i < 100; i++)
            Assert.Equal(original with { WasAlreadyReceived = true }, await fixture.Store.ReceiveAsync(fixture.Reference));
        var inbox = Assert.Single(await fixture.Auth.Db.GroupIngressInbox.ToListAsync());
        Assert.Equal(fixture.Auth.Service.Id, inbox.ServiceId);
        Assert.Equal(fixture.Auth.Grant.Version, inbox.GrantVersion);
        Assert.Equal((fixture.Reference.EventId, fixture.Reference.MessageId, fixture.Reference.Revision, fixture.Reference.CommittedSequence),
            (inbox.EventId, inbox.MessageId, inbox.Revision, inbox.CommittedSequence));
        Assert.Equal(original.ReceivedAtUtc, inbox.ReceivedAtUtc);
        Assert.Equal(0, fixture.State.ScheduledThroughSequence);
        Assert.Equal(fixture.Revision.CommittedAtUtc, fixture.State.FirstPendingAtUtc);
        Assert.Equal(fixture.Revision.CommittedAtUtc, fixture.State.LastPendingAtUtc);
        Assert.Equal(0, fixture.Outbox.PublishAttempts); Assert.Null(fixture.Outbox.PublishedAtUtc);
        Assert.Empty(await fixture.Auth.Db.Tasks.ToListAsync()); Assert.Empty(await fixture.Auth.Db.Users.ToListAsync());
        Assert.Equal(0, fixture.Auth.Secrets.Calls);
        Assert.Equal(Enumerable.Repeat((byte)0x11, 29), fixture.Revision.ProtectedContent);
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("ingest-only")]
    [InlineData("notify-only")]
    [InlineData("epoch")]
    [InlineData("service")]
    [InlineData("source")]
    [InlineData("source-version")]
    [InlineData("deletion")]
    [InlineData("role")]
    [InlineData("company")]
    [InlineData("account")]
    [InlineData("account-alias")]
    [InlineData("physical-hash")]
    public async Task FreshExtractionAuthorityDenialRetainsSqlBacklogBeforeAnyPrivateDependency(string change)
    {
        using var fixture = new Fixture();
        switch (change)
        {
            case "grant": fixture.Auth.Grant.IsEnabled = false; break;
            case "ingest-only":
            case "notify-only":
                fixture.Auth.Db.Remove(fixture.Auth.Grant);
                fixture.Auth.Db.Add(new GroupServiceGrantRecord
                {
                    TenantId = fixture.Auth.Scope.TenantId,
                    CompanyId = fixture.Auth.Scope.CompanyId,
                    ServiceId = fixture.Auth.Service.Id,
                    BindingId = fixture.Auth.Scope.SourceBindingId,
                    Version = 1,
                    IsEnabled = true,
                    Capability = change == "ingest-only" ? GroupServiceCapability.Ingest : GroupServiceCapability.Notify
                });
                break;
            case "epoch": fixture.Auth.Service.CredentialEpoch++; break;
            case "service": fixture.Auth.Service.IsEnabled = false; break;
            case "source": fixture.Auth.Binding.IsEnabled = false; break;
            case "source-version": fixture.Auth.Binding.Version++; break;
            case "deletion": fixture.Auth.Binding.DeletionGeneration++; break;
            case "role": fixture.Auth.Binding.Role = GroupBindingRole.TechnicalInternal; break;
            case "company": (await fixture.Auth.Db.Companies.SingleAsync()).IsActive = false; break;
            case "account": fixture.Auth.Account.IsEnabled = false; break;
            case "account-alias": fixture.Auth.Account.ExternalAccountId = fixture.Auth.External.AccountId.TrimEnd(); break;
            case "physical-hash": fixture.Auth.Binding.PhysicalGroupHash = new string('0', 64); break;
        }
        await fixture.Auth.Db.SaveChangesAsync();
        if (change is "source-version" or "deletion")
            Assert.Equal("Group inbox delivery is not available.",
                (await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.ReceiveAsync(fixture.Reference))).Message);
        else await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Store.ReceiveAsync(fixture.Reference));
        Assert.Empty(await fixture.Auth.Db.GroupIngressInbox.ToListAsync());
        Assert.Single(await fixture.Auth.Db.GroupIngressOutbox.ToListAsync());
        Assert.Equal(0, fixture.State.ScheduledThroughSequence); Assert.Equal(0, fixture.Auth.Secrets.Calls);
    }

    [Fact]
    public async Task CommittedDeliveryCanArriveDuringPublisherRetryReservationBeforeConfirmationIsRecorded()
    {
        using var fixture = new Fixture();
        fixture.Outbox.AvailableAtUtc = fixture.Auth.Clock.Current.AddSeconds(5);
        fixture.Outbox.PublishAttempts = 1;
        await fixture.Auth.Db.SaveChangesAsync();
        var original = await fixture.Store.ReceiveAsync(fixture.Reference);
        Assert.False(original.WasAlreadyReceived);
        Assert.Equal(original with { WasAlreadyReceived = true }, await fixture.Store.ReceiveAsync(fixture.Reference));
        Assert.Single(await fixture.Auth.Db.GroupIngressInbox.ToListAsync());
        Assert.Null(fixture.Outbox.PublishedAtUtc);
        Assert.Equal(1, fixture.Outbox.PublishAttempts);
        Assert.Equal(0, fixture.State.ScheduledThroughSequence);
        Assert.Equal(0, fixture.Auth.Secrets.Calls);
    }

    [Fact]
    public async Task AlreadyReceivedDeliveryStillRequiresCurrentGrantAndExactRestoredAuthority()
    {
        using var fixture = new Fixture(); var original = await fixture.Store.ReceiveAsync(fixture.Reference);
        fixture.Auth.Grant.IsEnabled = false; await fixture.Auth.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Store.ReceiveAsync(fixture.Reference));
        fixture.Auth.Grant.IsEnabled = true; await fixture.Auth.Db.SaveChangesAsync();
        Assert.Equal(original with { WasAlreadyReceived = true }, await fixture.Store.ReceiveAsync(fixture.Reference));
        Assert.Single(await fixture.Auth.Db.GroupIngressInbox.ToListAsync()); Assert.Equal(0, fixture.Auth.Secrets.Calls);
    }

    [Theory]
    [InlineData("missing-outbox")]
    [InlineData("outbox-message")]
    [InlineData("outbox-revision")]
    [InlineData("outbox-sequence")]
    [InlineData("non-utc-outbox")]
    [InlineData("uncommitted-sequence")]
    [InlineData("missing-revision")]
    [InlineData("future-revision")]
    [InlineData("missing-receipt")]
    [InlineData("receipt-time")]
    [InlineData("receipt-epoch")]
    public async Task BrokerHintCannotInventOrRetargetCommittedSqlGraph(string change)
    {
        using var fixture = new Fixture();
        switch (change)
        {
            case "missing-outbox": fixture.Auth.Db.Remove(fixture.Outbox); break;
            case "outbox-message": fixture.Outbox.MessageId = Guid.NewGuid(); break;
            case "outbox-revision": fixture.Outbox.Revision++; break;
            case "outbox-sequence": fixture.Outbox.CommittedSequence++; break;
            case "non-utc-outbox": fixture.Outbox.AvailableAtUtc = fixture.Outbox.AvailableAtUtc.ToOffset(TimeSpan.FromHours(1)); break;
            case "uncommitted-sequence": fixture.State.CommittedSequence = 0; break;
            case "missing-revision":
                // InMemory-only corruption control. SQL restricts this orphan;
                // clear tracked dependents so EF does not repair the fixture.
                fixture.Auth.Db.ChangeTracker.Clear(); fixture.Auth.Db.Remove(fixture.Revision); break;
            case "future-revision": fixture.Revision.CommittedAtUtc = fixture.Auth.Clock.Current.AddSeconds(1); break;
            case "missing-receipt": fixture.Auth.Db.Remove(fixture.Receipt); break;
            case "receipt-time": fixture.Receipt.CommittedAtUtc += TimeSpan.FromMilliseconds(1); break;
            case "receipt-epoch": fixture.Receipt.CredentialEpoch = 0; break;
        }
        await fixture.Auth.Db.SaveChangesAsync();
        Assert.Equal("Group inbox delivery is not available.",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.ReceiveAsync(fixture.Reference))).Message);
        Assert.Empty(await fixture.Auth.Db.GroupIngressInbox.ToListAsync()); Assert.Equal(0, fixture.Auth.Secrets.Calls);
    }

    [Fact]
    public async Task ForeignHostCompanyAndCanceledCallFailBeforeEvenOpeningDatabase()
    {
        using var fixture = new Fixture(); fixture.Auth.Db.Dispose();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Store.ReceiveAsync(fixture.Reference with
        { Source = fixture.Reference.Source with { CompanyId = Guid.NewGuid() } }));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Store.ReceiveAsync(fixture.Reference, canceled.Token));
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly GroupServiceAuthenticatorTests.Fixture Auth = new("capability");
        internal readonly GroupIngressDispatchReference Reference;
        internal readonly GroupIngressOutboxRecord Outbox;
        internal readonly GroupMessageRevisionRecord Revision;
        internal readonly GroupIngressReceiptRecord Receipt;
        internal readonly GroupSourceStateRecord State;
        internal GroupIngressInboxStore Store => new(Auth.Db, new(Auth.Scope.TenantId, Auth.Scope.CompanyId, Auth.Service.Id, 1), Auth.Clock);

        internal Fixture()
        {
            var scope = Auth.Scope; var messageId = Guid.NewGuid();
            Reference = new(1, scope, Guid.NewGuid(), messageId, 1, 1);
            var committed = Auth.Clock.Current.AddSeconds(-1);
            Outbox = new()
            {
                TenantId = scope.TenantId,
                CompanyId = scope.CompanyId,
                BindingId = scope.SourceBindingId,
                Id = Reference.EventId,
                MessageId = messageId,
                Revision = 1,
                CommittedSequence = 1,
                AvailableAtUtc = committed
            };
            Revision = new()
            {
                TenantId = scope.TenantId,
                CompanyId = scope.CompanyId,
                BindingId = scope.SourceBindingId,
                MessageId = messageId,
                Revision = 1,
                CommittedSequence = 1,
                SourceVersion = 1,
                DeletionGeneration = 0,
                Kind = GroupSourceEventKind.NewText,
                ExternalRevisionEventId = "owned-inert-event",
                SenderId = "owned-inert-sender",
                ContentKeyId = "owned-inert-key",
                ContentSha256 = new string('B', 64),
                ProtectedContent = Enumerable.Repeat((byte)0x11, 29).ToArray(),
                OccurredAtUtc = committed,
                CommittedAtUtc = committed
            };
            Receipt = new()
            {
                TenantId = scope.TenantId,
                CompanyId = scope.CompanyId,
                BindingId = scope.SourceBindingId,
                MessageId = messageId,
                Revision = 1,
                EventIdentityHash = GroupIngressIdentity.EventIndex(scope, "owned-inert-event"),
                EnvelopeSha256 = new string('C', 64),
                ExternalRevisionEventId = "owned-inert-event",
                ServiceId = Auth.Service.Id,
                CredentialEpoch = 1,
                ListenerEpoch = 1,
                CommittedAtUtc = committed
            };
            State = new()
            {
                TenantId = scope.TenantId,
                CompanyId = scope.CompanyId,
                BindingId = scope.SourceBindingId,
                CommittedSequence = 1,
                FirstPendingAtUtc = committed,
                LastPendingAtUtc = committed
            };
            var message = new GroupMessageRecord
            {
                TenantId = scope.TenantId,
                CompanyId = scope.CompanyId,
                BindingId = scope.SourceBindingId,
                Id = messageId,
                ExternalMessageId = "owned-inert-message",
                IdentityHash = GroupIngressIdentity.MessageIndex(scope, "owned-inert-message")
            };
            Auth.Db.AddRange(message, Revision, Receipt, Outbox, State); Auth.Db.SaveChanges();
        }

        public void Dispose() => Auth.Dispose();
    }
}
