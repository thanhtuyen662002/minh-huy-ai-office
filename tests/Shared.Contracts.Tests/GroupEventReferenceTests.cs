using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Shared.Contracts.Tests;

public sealed class GroupEventReferenceTests
{
    private static GroupSourceEventMetadata Event => new(new("synthetic", "account ", "customer "),
        "message ", "revision ", "sender ", "reply ", GroupSourceEventKind.NewText,
        new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero), new string('A', 64), false);

    [Fact]
    public void Source_metadata_preserves_all_opaque_ids_and_unknown_media_without_claiming_understanding()
    {
        Event.Validate();
        (Event with { Kind = GroupSourceEventKind.Media }).Validate();
        Assert.Equal("sender ", Event.SenderId);
        Assert.Equal("reply ", Event.ReplyToMessageId);
        Assert.Throws<InvalidOperationException>(() => (Event with { SenderId = "" }).Validate());
        Assert.Throws<InvalidOperationException>(() => (Event with { RevisionEventId = new string((char)0xD800, 1) }).Validate());
        Assert.Throws<InvalidOperationException>(() => (Event with { ContentSha256 = "bad" }).Validate());
        Assert.Throws<InvalidOperationException>(() => (Event with { OccurredAtUtc = Event.OccurredAtUtc.ToOffset(TimeSpan.FromHours(7)) }).Validate());
    }

    private static (GroupNotificationAuthorization Authorization, GroupNotesCommitted Notes) Packet(bool backfill = false)
    {
        var scope = new GroupScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var service = Guid.NewGuid();
        var source = new SourceGroupBinding(scope, Guid.NewGuid(), new("synthetic", "source-account", "source"), "Same name", 2, 0, true);
        var destination = new TechnicalDestinationBinding(scope.TenantId, scope.CompanyId, Guid.NewGuid(), Guid.NewGuid(),
            new("synthetic", "IT-account", "internal-IT"), "Same name", 3, true);
        var snapshot = new GroupRouteSnapshot(source, destination, new(scope, destination.Id, 4, 2, 3, true), new(scope, destination.Id, 4, 5, true));
        var authorization = GroupRoutingPolicy.AuthorizeNotification(new(service, 1), new(scope.TenantId, scope.CompanyId, service, 1, true),
            new(scope.TenantId, scope.CompanyId, service, destination.Id, GroupServiceCapability.Notify, 1, true), [snapshot], [source.ExternalIdentity]);
        var batch = FrozenGroupBatch.Create(Guid.NewGuid(), scope, 1, GroupCoverage.PartialMedia, backfill,
            [new(Guid.NewGuid(), 1, 1, new string('A', 64))]);
        return (authorization, GroupNotesCommitted.Create(Guid.NewGuid(), batch, [new(Guid.NewGuid(), 1, "REQ-1")]));
    }

    [Fact]
    public void Report_intent_binds_it_route_and_scoped_committed_revisions_without_raw_source_or_approval()
    {
        var packet = Packet();
        var intent = GroupTechnicalReportIntent.Create(Guid.NewGuid(), 1, new string('B', 64), packet.Authorization, [packet.Notes]);
        Assert.Equal(packet.Authorization.Destination, intent.Destination);
        Assert.Equal(packet.Notes.Scope, Assert.Single(intent.Notes).Source);
        Assert.Equal(packet.Notes.Notes[0].NoteId, intent.Notes[0].NoteId);
        Assert.Equal(packet.Notes.EventId, Assert.Single(intent.CommittedEventIds));
        Assert.DoesNotContain(typeof(GroupTechnicalReportIntent).GetProperties(), x => x.Name is "Approval" or "MessageText" or "UserId");
    }

    [Fact]
    public void Backfill_foreign_source_and_duplicate_event_cannot_create_report_intent()
    {
        var packet = Packet();
        var backfill = Packet(true);
        Assert.Throws<UnauthorizedAccessException>(() => GroupTechnicalReportIntent.Create(Guid.NewGuid(), 1, new string('B', 64), backfill.Authorization, [backfill.Notes]));
        Assert.Throws<UnauthorizedAccessException>(() => GroupTechnicalReportIntent.Create(Guid.NewGuid(), 1, new string('B', 64), packet.Authorization, [Packet().Notes]));
        Assert.Throws<UnauthorizedAccessException>(() => GroupTechnicalReportIntent.Create(Guid.NewGuid(), 1, new string('B', 64), packet.Authorization, [packet.Notes, packet.Notes]));
    }
}
