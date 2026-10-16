namespace MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

public enum GroupSourceEventKind { NewText = 1, Media = 2, Edit = 3, Recall = 4 }

/// <summary>Metadata alongside protected source content, not a portal-user authority.</summary>
public sealed record GroupSourceEventMetadata(GroupExternalIdentity Identity,
    string MessageId, string RevisionEventId, string SenderId, string? ReplyToMessageId,
    GroupSourceEventKind Kind, DateTimeOffset OccurredAtUtc, string ContentSha256,
    bool IsHistoricalBackfill)
{
    public void Validate()
    {
        if (Identity is null || !Enum.IsDefined(Kind) || OccurredAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("Source event is invalid.");
        Identity.Validate();
        GroupExternalIdentity.ValidateOpaqueId(MessageId);
        GroupExternalIdentity.ValidateOpaqueId(RevisionEventId);
        GroupExternalIdentity.ValidateOpaqueId(SenderId);
        if (ReplyToMessageId is not null) GroupExternalIdentity.ValidateOpaqueId(ReplyToMessageId);
        GroupWorkflowValidation.RequireSha256(ContentSha256);
    }
}

public sealed record GroupScopedNoteReference(GroupScope Source, Guid NoteId,
    long NoteRevision, string RequestCode);

/// <summary>SQL-driven report references from already authorized committed packets.</summary>
public sealed class GroupTechnicalReportIntent
{
    private GroupTechnicalReportIntent(Guid id, long revision, string contentSha256,
        GroupNotificationAuthorization authorization, IReadOnlyList<GroupScopedNoteReference> notes,
        IReadOnlyList<Guid> committedEventIds)
    {
        Id = id;
        Revision = revision;
        ContentSha256 = contentSha256;
        Destination = authorization.Destination;
        Routes = authorization.Routes;
        Notes = notes;
        CommittedEventIds = committedEventIds;
    }
    public const int MaximumNotes = 200;
    public Guid Id { get; }
    public long Revision { get; }
    public string ContentSha256 { get; }
    public TechnicalDestinationBinding Destination { get; }
    public IReadOnlyList<GroupRouteVersion> Routes { get; }
    public IReadOnlyList<GroupScopedNoteReference> Notes { get; }
    public IReadOnlyList<Guid> CommittedEventIds { get; }

    // A factory cannot prove SQL commit. The reporter must resolve these references
    // from current SQL, render/mask the body and recheck all authority before send.
    public static GroupTechnicalReportIntent Create(Guid id, long revision, string contentSha256,
        GroupNotificationAuthorization authorization, IReadOnlyList<GroupNotesCommitted> committedEvents)
    {
        if (id == Guid.Empty || revision <= 0 || authorization is null ||
            committedEvents is null || committedEvents.Count is < 1 or > MaximumNotes)
            throw new InvalidOperationException("Technical report intent is invalid.");
        GroupWorkflowValidation.RequireSha256(contentSha256);
        var sourceIds = new HashSet<Guid>();
        var events = new HashSet<Guid>();
        var noteIds = new HashSet<(Guid, Guid)>();
        var notes = new List<GroupScopedNoteReference>();
        foreach (var committed in committedEvents)
        {
            if (committed is null || !committed.MayNotify || !events.Add(committed.EventId) ||
                committed.Scope.TenantId != authorization.Destination.TenantId ||
                committed.Scope.CompanyId != authorization.Destination.CompanyId ||
                !authorization.Routes.Any(x => x.SourceBindingId == committed.Scope.SourceBindingId))
                throw new UnauthorizedAccessException("Technical report references are not authorized.");
            sourceIds.Add(committed.Scope.SourceBindingId);
            foreach (var note in committed.Notes)
            {
                if (!noteIds.Add((committed.Scope.SourceBindingId, note.NoteId)))
                    throw new InvalidOperationException("Technical report note revisions are ambiguous.");
                notes.Add(new(committed.Scope, note.NoteId, note.Revision, note.RequestCode));
                if (notes.Count > MaximumNotes)
                    throw new InvalidOperationException("Technical report exceeds the note bound; preserve backlog.");
            }
        }
        if (!sourceIds.SetEquals(authorization.Routes.Select(x => x.SourceBindingId)))
            throw new InvalidOperationException("Technical report route snapshot is not exact.");
        return new(id, revision, contentSha256, authorization,
            Array.AsReadOnly(notes.OrderBy(x => x.Source.SourceBindingId).ThenBy(x => x.NoteId).ToArray()),
            Array.AsReadOnly(events.Order().ToArray()));
    }
}
