using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupIngressOutboxDispatcherTests
{
    [Fact]
    public async Task ConfirmedReferenceRetriesWithOriginalIdentityUntilDurableInboxAndNeverAdvancesBatchCursor()
    {
        using var fixture = new Fixture();
        Assert.True(await fixture.Dispatcher.PublishNextAsync(fixture.Auth.Scope));
        var first = Assert.Single(fixture.Publisher.References);
        var row = await fixture.OutboxAsync();
        Assert.Equal((1, fixture.Auth.Clock.Current, fixture.Auth.Clock.Current + GroupIngressOutboxDispatcher.RetryDelay),
            (row.PublishAttempts, row.PublishedAtUtc, row.AvailableAtUtc));
        Assert.False(await fixture.Dispatcher.PublishNextAsync(fixture.Auth.Scope));
        fixture.Auth.Clock.Current += GroupIngressOutboxDispatcher.RetryDelay;
        Assert.True(await fixture.Dispatcher.PublishNextAsync(fixture.Auth.Scope));
        Assert.Equal(first, fixture.Publisher.References[1]);
        await fixture.Inbox.ReceiveAsync(first);
        fixture.Auth.Clock.Current += GroupIngressOutboxDispatcher.RetryDelay;
        Assert.False(await fixture.Dispatcher.PublishNextAsync(fixture.Auth.Scope));
        Assert.Equal(2, fixture.Publisher.References.Count);
        await fixture.RequireNoPrivateOrCursorEffectsAsync();
    }

    [Fact]
    public async Task LostConfirmRetainsCommittedRetryAndFreshDispatcherRepublishesExactReference()
    {
        using var fixture = new Fixture();
        fixture.Publisher.Action = _ => throw new IOException("owned-private-publisher-detail");
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Dispatcher.PublishNextAsync(fixture.Auth.Scope));
        Assert.Equal("Group reference publication is not available.", failure.Message); Assert.Null(failure.InnerException);
        var original = Assert.Single(fixture.Publisher.References);
        var reserved = await fixture.OutboxAsync();
        Assert.Equal(1, reserved.PublishAttempts); Assert.Null(reserved.PublishedAtUtc);
        Assert.False(fixture.Auth.Db.ChangeTracker.HasChanges());
        fixture.Auth.Clock.Current += GroupIngressOutboxDispatcher.RetryDelay;
        fixture.Publisher.Action = null;
        Assert.True(await fixture.Dispatcher.PublishNextAsync(fixture.Auth.Scope));
        Assert.Equal(original, fixture.Publisher.References[1]);
        Assert.Equal(2, (await fixture.OutboxAsync()).PublishAttempts);
        await fixture.RequireNoPrivateOrCursorEffectsAsync();
    }

    [Fact]
    public async Task DeliveryCommittedBeforeLostConfirmationStopsPublisherDespiteAbsentPublishedTimestamp()
    {
        using var fixture = new Fixture();
        fixture.Publisher.Action = async reference =>
        {
            var received = await fixture.Inbox.ReceiveAsync(reference);
            Assert.False(received.WasAlreadyReceived);
            throw new IOException("owned-lost-confirm-after-inbox");
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Dispatcher.PublishNextAsync(fixture.Auth.Scope));
        Assert.Null((await fixture.OutboxAsync()).PublishedAtUtc);
        Assert.Single(await fixture.Auth.Db.GroupIngressInbox.ToListAsync());
        fixture.Auth.Clock.Current += GroupIngressOutboxDispatcher.RetryDelay;
        Assert.False(await fixture.Dispatcher.PublishNextAsync(fixture.Auth.Scope));
        Assert.Single(fixture.Publisher.References);
        await fixture.RequireNoPrivateOrCursorEffectsAsync();
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("epoch")]
    [InlineData("source")]
    public async Task CurrentAuthorityLossBeforeReservationNeverInvokesPublisherOrMutatesOutbox(string change)
    {
        using var fixture = new Fixture();
        if (change == "grant") fixture.Auth.Grant.IsEnabled = false;
        else if (change == "epoch") fixture.Auth.Service.CredentialEpoch++;
        else fixture.Auth.Binding.IsEnabled = false;
        await fixture.Auth.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Dispatcher.PublishNextAsync(fixture.Auth.Scope));
        Assert.Empty(fixture.Publisher.References);
        Assert.Equal(0, (await fixture.OutboxAsync()).PublishAttempts);
        Assert.False(fixture.Auth.Db.ChangeTracker.HasChanges());
        await fixture.RequireNoPrivateOrCursorEffectsAsync();
    }

    [Fact]
    public async Task RevocationDuringPublicationKeepsBacklogAndCannotRecordConfirmationUntilExactRestoration()
    {
        using var fixture = new Fixture();
        fixture.Publisher.Action = async _ =>
        {
            fixture.Auth.Grant.IsEnabled = false; await fixture.Auth.Db.SaveChangesAsync();
        };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Dispatcher.PublishNextAsync(fixture.Auth.Scope));
        var original = Assert.Single(fixture.Publisher.References);
        Assert.Null((await fixture.OutboxAsync()).PublishedAtUtc);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Inbox.ReceiveAsync(original));
        Assert.Empty(await fixture.Auth.Db.GroupIngressInbox.ToListAsync());
        fixture.Auth.Grant.IsEnabled = true; await fixture.Auth.Db.SaveChangesAsync();
        fixture.Auth.Clock.Current += GroupIngressOutboxDispatcher.RetryDelay; fixture.Publisher.Action = null;
        Assert.True(await fixture.Dispatcher.PublishNextAsync(fixture.Auth.Scope));
        Assert.Equal(original, fixture.Publisher.References[1]);
        Assert.False((await fixture.Inbox.ReceiveAsync(original)).WasAlreadyReceived);
        await fixture.RequireNoPrivateOrCursorEffectsAsync();
    }

    [Fact]
    public async Task DelayedConfirmationCannotOverwriteNewerDurableAttempt()
    {
        using var fixture = new Fixture();
        var newerPublisher = new Publisher();
        var newer = new GroupIngressOutboxDispatcher(fixture.Auth.Db, fixture.Worker, newerPublisher, fixture.Auth.Clock);
        DateTimeOffset? newerConfirmation = null;
        fixture.Publisher.Action = async reference =>
        {
            fixture.Auth.Clock.Current += GroupIngressOutboxDispatcher.RetryDelay;
            Assert.True(await newer.PublishNextAsync(fixture.Auth.Scope));
            Assert.Equal(reference, Assert.Single(newerPublisher.References));
            newerConfirmation = (await fixture.OutboxAsync()).PublishedAtUtc;
            fixture.Auth.Clock.Current += TimeSpan.FromSeconds(1);
        };
        Assert.True(await fixture.Dispatcher.PublishNextAsync(fixture.Auth.Scope));
        var row = await fixture.OutboxAsync();
        Assert.Equal(2, row.PublishAttempts); Assert.Equal(newerConfirmation, row.PublishedAtUtc);
        Assert.NotEqual(fixture.Auth.Clock.Current, row.PublishedAtUtc);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("future-revision")]
    [InlineData("uncommitted")]
    public async Task CorruptOrUncommittedSqlMetadataIsNeverReservedOrPublished(string change)
    {
        using var fixture = new Fixture();
        if (change == "version") fixture.Revision.SourceVersion++;
        else if (change == "future-revision") fixture.Revision.CommittedAtUtc = fixture.Auth.Clock.Current.AddSeconds(1);
        else fixture.State.CommittedSequence = 0;
        await fixture.Auth.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Dispatcher.PublishNextAsync(fixture.Auth.Scope));
        Assert.Empty(fixture.Publisher.References); Assert.Equal(0, (await fixture.OutboxAsync()).PublishAttempts);
        Assert.False(fixture.Auth.Db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task UnchangedPretrackedOutboxCannotOverwriteReservationCommittedByAnotherContext()
    {
        using var fixture = new Fixture();
        var unchangedGrant = fixture.Auth.Db.Entry(fixture.Auth.Grant);
        await using (var other = new PlatformDbContext(fixture.Auth.Options))
        {
            var current = await other.GroupIngressOutbox.SingleAsync();
            current.PublishAttempts = 5; current.AvailableAtUtc = fixture.Auth.Clock.Current;
            await other.SaveChangesAsync();
        }
        Assert.False(fixture.Auth.Db.ChangeTracker.HasChanges());
        Assert.True(await fixture.Dispatcher.PublishNextAsync(fixture.Auth.Scope));
        Assert.Equal(6, (await fixture.OutboxAsync()).PublishAttempts);
        Assert.Equal(EntityState.Unchanged, unchangedGrant.State);
        Assert.Same(fixture.Auth.Grant, unchangedGrant.Entity);
        Assert.False(fixture.Auth.Db.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmationReadsActualReservationEvenIfPublisherPretrackedAnOlderRow(bool newerConfirmed)
    {
        using var fixture = new Fixture();
        var current = fixture.Auth.Clock.Current;
        fixture.Publisher.Action = async _ =>
        {
            var old = await fixture.Auth.Db.GroupIngressOutbox.SingleAsync();
            Assert.Equal(1, old.PublishAttempts);
            await using var other = new PlatformDbContext(fixture.Auth.Options);
            var newer = await other.GroupIngressOutbox.SingleAsync();
            newer.PublishAttempts = 7; newer.AvailableAtUtc = current.AddSeconds(8);
            newer.PublishedAtUtc = newerConfirmed ? current.AddSeconds(2) : null;
            await other.SaveChangesAsync();
            fixture.Auth.Clock.Current = current.AddSeconds(3);
            Assert.False(fixture.Auth.Db.ChangeTracker.HasChanges());
        };
        Assert.True(await fixture.Dispatcher.PublishNextAsync(fixture.Auth.Scope));
        var confirmed = await fixture.OutboxAsync();
        Assert.Equal(7, confirmed.PublishAttempts);
        Assert.Equal(current.AddSeconds(8), confirmed.AvailableAtUtc);
        Assert.Equal(newerConfirmed ? current.AddSeconds(2) : (DateTimeOffset?)null, confirmed.PublishedAtUtc);
        Assert.False(fixture.Auth.Db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task ForeignHostAndCallerCancellationRefuseBeforeDatabaseOrPublisher()
    {
        using var fixture = new Fixture(); fixture.Auth.Db.Dispose();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Dispatcher.PublishNextAsync(fixture.Auth.Scope with { CompanyId = Guid.NewGuid() }));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Dispatcher.PublishNextAsync(fixture.Auth.Scope, canceled.Token));
        Assert.Empty(fixture.Publisher.References);
    }

    [Fact]
    public async Task NoncooperativePublisherIsBoundedAndLateCompletionCannotRecordConfirmation()
    {
        using var fixture = new Fixture();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Publisher.Action = _ => held.Task;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Dispatcher.PublishNextAsync(fixture.Auth.Scope));
            Assert.Equal("Group reference publication is not available.", failure.Message);
            Assert.InRange(stopwatch.Elapsed.TotalSeconds, 9, 15);
            Assert.Null((await fixture.OutboxAsync()).PublishedAtUtc);
            Assert.Equal(1, (await fixture.OutboxAsync()).PublishAttempts);
            Assert.False(fixture.Auth.Db.ChangeTracker.HasChanges());
        }
        finally { held.TrySetException(new IOException("owned-late-private-failure")); }
        Assert.Null((await fixture.OutboxAsync()).PublishedAtUtc);
    }

    internal sealed class Publisher : IGroupIngressReferencePublisher
    {
        internal readonly List<GroupIngressDispatchReference> References = [];
        internal Func<GroupIngressDispatchReference, Task>? Action;
        public Task PublishAsync(GroupIngressDispatchReference reference, CancellationToken cancellationToken)
        {
            References.Add(reference);
            return Action?.Invoke(reference) ?? Task.CompletedTask;
        }
    }

    // Owned inert graph; InMemory does not establish native SQL locking,
    // permissions, crash recovery or a real RabbitMQ confirmation.
    internal sealed class Fixture : IDisposable
    {
        internal readonly GroupServiceAuthenticatorTests.Fixture Auth = new("capability");
        internal readonly Publisher Publisher = new();
        internal readonly GroupMessageRevisionRecord Revision;
        internal readonly GroupSourceStateRecord State;
        internal GroupExtractionWorkerBinding Worker => new(Auth.Scope.TenantId, Auth.Scope.CompanyId, Auth.Service.Id, 1);
        internal GroupIngressOutboxDispatcher Dispatcher => new(Auth.Db, Worker, Publisher, Auth.Clock);
        internal GroupIngressInboxStore Inbox => new(Auth.Db, Worker, Auth.Clock);
        internal Task<GroupIngressOutboxRecord> OutboxAsync() => Auth.Db.GroupIngressOutbox.AsNoTracking().SingleAsync();

        internal Fixture()
        {
            var scope = Auth.Scope; var messageId = Guid.NewGuid(); var committed = Auth.Clock.Current.AddSeconds(-1);
            var message = new GroupMessageRecord
            {
                TenantId = scope.TenantId,
                CompanyId = scope.CompanyId,
                BindingId = scope.SourceBindingId,
                Id = messageId,
                ExternalMessageId = "owned-outbox-message",
                IdentityHash = GroupIngressIdentity.MessageIndex(scope, "owned-outbox-message")
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
                ExternalRevisionEventId = "owned-outbox-event",
                SenderId = "owned-outbox-sender",
                ContentKeyId = "owned-inert-key",
                ContentSha256 = new string('B', 64),
                ProtectedContent = Enumerable.Repeat((byte)0x11, 29).ToArray(),
                OccurredAtUtc = committed,
                CommittedAtUtc = committed
            };
            var receipt = new GroupIngressReceiptRecord
            {
                TenantId = scope.TenantId,
                CompanyId = scope.CompanyId,
                BindingId = scope.SourceBindingId,
                MessageId = messageId,
                Revision = 1,
                EventIdentityHash = GroupIngressIdentity.EventIndex(scope, "owned-outbox-event"),
                EnvelopeSha256 = new string('C', 64),
                ExternalRevisionEventId = "owned-outbox-event",
                ServiceId = Auth.Service.Id,
                CredentialEpoch = 1,
                ListenerEpoch = 1,
                CommittedAtUtc = committed
            };
            var outbox = new GroupIngressOutboxRecord
            {
                TenantId = scope.TenantId,
                CompanyId = scope.CompanyId,
                BindingId = scope.SourceBindingId,
                Id = Guid.NewGuid(),
                MessageId = messageId,
                Revision = 1,
                CommittedSequence = 1,
                AvailableAtUtc = committed
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
            Auth.Db.AddRange(message, Revision, receipt, outbox, State); Auth.Db.SaveChanges();
        }

        internal async Task RequireNoPrivateOrCursorEffectsAsync()
        {
            var revision = await Auth.Db.GroupMessageRevisions.AsNoTracking().SingleAsync();
            Assert.Equal(Enumerable.Repeat((byte)0x11, 29), revision.ProtectedContent);
            var state = await Auth.Db.GroupSourceStates.AsNoTracking().SingleAsync();
            Assert.Equal(0, state.ScheduledThroughSequence); Assert.Equal(1, state.CommittedSequence);
            Assert.Equal(revision.CommittedAtUtc, state.FirstPendingAtUtc); Assert.Equal(revision.CommittedAtUtc, state.LastPendingAtUtc);
            Assert.Equal(0, Auth.Secrets.Calls);
            Assert.Empty(await Auth.Db.Users.ToListAsync()); Assert.Empty(await Auth.Db.Tasks.ToListAsync());
        }

        public void Dispose() => Auth.Dispose();
    }
}
